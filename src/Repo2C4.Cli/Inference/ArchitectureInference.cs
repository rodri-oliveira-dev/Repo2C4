using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Cli.Inference;

/// <summary>A provider receives only a sanitized snapshot and returns a reviewable v1 proposal.</summary>
public interface IArchitectureInferenceProvider
{
    Task<ArchitectureModel> InferAsync(RepositorySnapshot sanitizedSnapshot, CancellationToken cancellationToken);
}

public enum InferenceFailure
{
    InvalidInput,
    InvalidResponse,
    Unavailable,
    TimedOut,
    PayloadTooLarge,
}

public sealed class InferenceException : Exception
{
    public InferenceException(InferenceFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public InferenceFailure Failure
    {
        get;
    }
}

/// <summary>
/// Drops all untrusted file names, source text, diagnostics and free-form descriptions.
/// Only bounded, fixed-vocabulary facts and opaque evidence IDs leave the CLI process.
/// </summary>
public static class InferenceSnapshotSanitizer
{
    private const int MaxFiles = 256;
    private const int MaxEvidence = 512;

    public static RepositorySnapshot Sanitize(RepositorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _ = ContractJson.SerializeSnapshot(snapshot);

        if (snapshot.Files.Length > MaxFiles || snapshot.Evidence.Length > MaxEvidence)
        {
            throw new InferenceException(
                InferenceFailure.PayloadTooLarge,
                "Snapshot exceeds the inference file/evidence limits.");
        }

        Dictionary<string, string> aliases = new(StringComparer.Ordinal);
        ImmutableArray<RepositoryFile>.Builder files = ImmutableArray.CreateBuilder<RepositoryFile>();
        int fileIndex = 0;
        foreach (RepositoryFile file in snapshot.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            string alias = "files/file_" + (++fileIndex).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            aliases.Add(file.RelativePath, alias);
            files.Add(new RepositoryFile(alias, 0, null));
        }

        ImmutableArray<Evidence>.Builder evidence = ImmutableArray.CreateBuilder<Evidence>();
        foreach (Evidence item in snapshot.Evidence.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            if (!TryDescribe(item, out string? description))
            {
                continue;
            }

            evidence.Add(new Evidence(
                OpaqueId("ev", item.Id),
                item.Category,
                aliases[item.RelativePath],
                null,
                item.SourceType,
                description));
        }

        return new RepositorySnapshot(snapshot.SchemaVersion, OpaqueId("repo", snapshot.RepositoryId), files.ToImmutable(), evidence.ToImmutable(), []);
    }

    internal static string OpaqueId(string prefix, string value) =>
        prefix + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];

    private static bool TryDescribe(Evidence evidence, out string? description)
    {
        description = evidence.Category switch
        {
            "dotnet.project" => "An MSBuild project manifest exists.",
            "dotnet.solution.project" => "A solution references an inventoried project.",
            "dotnet.project.kind" => DescribeKind(evidence.Description),
            "dotnet.project.targetFramework" => "A target framework declaration exists.",
            "dotnet.project.reference" => "A build-time project reference exists; runtime communication is unverified.",
            "dotnet.project.testCandidate" => "A test-project candidate exists.",
            "dotnet.runtime.http.candidate" => "HTTP host or route candidate; runtime usage is unverified.",
            "dotnet.runtime.worker.candidate" => "Worker host candidate; runtime execution is unverified.",
            "dotnet.integration.postgresql.candidate" => "PostgreSQL integration candidate; connection unverified.",
            "dotnet.integration.rabbitmq.candidate" => "RabbitMQ integration candidate; connection unverified.",
            "dotnet.integration.redis.candidate" => "Redis integration candidate; connection unverified.",
            "deployment.docker.manifest" => "A Docker-related manifest exists; deployment unverified.",
            _ => null,
        };
        return description is not null;
    }

    private static string DescribeKind(string untrustedDescription)
    {
        string[] allowedKinds = ["Library", "Executable", "Web", "Worker", "Test"];
        foreach (string kind in allowedKinds)
        {
            if (untrustedDescription.Equals("Project declaration indicates kind: " + kind + ".", StringComparison.Ordinal))
            {
                return "Project kind candidate: " + kind + ".";
            }
        }

        return "Project kind declaration exists; specific kind unverified.";
    }
}

/// <summary>Restores the original locally validated snapshot and forces AI assertions through human review.</summary>
public static class ArchitectureInference
{
    private const string ReviewReason = "AI-generated proposal; verify the architecture and cited evidence before accepting it.";

    public static async Task<ArchitectureModel> ProposeAsync(
        RepositorySnapshot snapshot,
        IArchitectureInferenceProvider provider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(provider);

        RepositorySnapshot sanitized = InferenceSnapshotSanitizer.Sanitize(snapshot);
        ArchitectureModel inferred = await provider.InferAsync(sanitized, cancellationToken).ConfigureAwait(false);

        // The provider cannot add evidence, replace inventory, or claim that its interpretations are proven.
        string expected = ContractJson.SerializeSnapshot(sanitized);
        if (!string.Equals(expected, ContractJson.SerializeSnapshot(inferred.Snapshot), StringComparison.Ordinal))
        {
            throw new InferenceException(InferenceFailure.InvalidResponse, "Provider returned an altered evidence snapshot.");
        }

        Dictionary<string, string> originalEvidenceIds = snapshot.Evidence.ToDictionary(
            item => InferenceSnapshotSanitizer.OpaqueId("ev", item.Id),
            item => item.Id,
            StringComparer.Ordinal);
        ArchitectureModel candidate = inferred with
        {
            Snapshot = snapshot,
            Elements =
            [
                .. inferred.Elements.Select(element => element with
                {
                    Status = ReviewStatus.RequiresReview,
                    ReviewReason = ReviewReason,
                    EvidenceIds = [.. element.EvidenceIds.Select(id => originalEvidenceIds[id])],
                }),
            ],
            Relations =
            [
                .. inferred.Relations.Select(relation => relation with
                {
                    Status = ReviewStatus.RequiresReview,
                    ReviewReason = ReviewReason,
                    EvidenceIds = [.. relation.EvidenceIds.Select(id => originalEvidenceIds[id])],
                }),
            ],
        };
        _ = ContractJson.SerializeModel(candidate);
        return candidate;
    }
}
