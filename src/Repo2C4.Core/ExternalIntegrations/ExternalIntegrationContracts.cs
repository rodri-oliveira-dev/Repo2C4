using System.Collections.Immutable;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.ExternalIntegrations;

/// <summary>InspectionReport compatibility accepted by the external integration boundary.</summary>
public static class ExternalIntegrationReportSchema
{
    public const int SupportedMajor = 1;

    public const int MinimumMinor = 6;

    public const string MinimumVersion = "1.6";
}

public enum ExternalIntegrationKind
{
    Http,
    Messaging,
    Database,
    Cache,
    Storage,
    Rpc,
    Unknown,
}

public enum ExternalIntegrationDirection
{
    Outbound,
    Publish,
    Consume,
    Read,
    Write,
    Bidirectional,
    Unknown,
}

public enum ExternalIntegrationConfidence
{
    High,
    Medium,
    Low,
}

/// <summary>Repository-relative provenance imported from one normalized integration finding.</summary>
public sealed record ExternalIntegrationEvidence(
    string Id,
    string Category,
    string Description,
    string? OriginalFindingId,
    string ProjectPath,
    ExternalIntegrationKind Kind,
    ExternalIntegrationDirection Direction,
    string Technology,
    string? Target,
    string? ResourceType,
    string? ConfigurationKey,
    string? Contract,
    string SourcePath,
    int SourceLine,
    ExternalIntegrationConfidence Confidence,
    ImmutableArray<string> Signals)
{
    public Evidence ToRepositoryEvidence() =>
        new(Id, Category, SourcePath, SourceLine, EvidenceSourceType.SourceCode, Description);
}

/// <summary>A controlled import diagnostic that never contains report or source bodies.</summary>
public sealed record ExternalIntegrationDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Path,
    string Message);

/// <summary>Bounded normalized output; no raw JSON or secret-bearing configuration values are retained.</summary>
public sealed record ExternalIntegrationEvidenceResult(
    string ReportSchemaVersion,
    string? RepositoryName,
    string? CommitSha,
    bool DiscoveryCompleted,
    bool Truncated,
    ImmutableArray<ExternalIntegrationEvidence> Evidence,
    ImmutableArray<ExternalIntegrationDiagnostic> Diagnostics)
{
    public ImmutableArray<Evidence> RepositoryEvidence =>
        [.. Evidence.Select(item => item.ToRepositoryEvidence())];
}

/// <summary>The validated snapshot produced by importing normalized external integration evidence.</summary>
public sealed record ExternalIntegrationSnapshotImportResult(
    RepositorySnapshot Snapshot,
    int ImportedEvidenceCount,
    ImmutableArray<ExternalIntegrationDiagnostic> Diagnostics)
{
    public bool Succeeded => Diagnostics.All(item => item.Severity != DiagnosticSeverity.Error);
}

/// <summary>Imports one bounded report and merges only normalized evidence into a repository snapshot.</summary>
public static class ExternalIntegrationSnapshotImporter
{
    public static async ValueTask<ExternalIntegrationSnapshotImportResult> ImportAsync(
        RepositorySnapshot snapshot,
        Stream report,
        ExternalIntegrationEvidenceReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(report);

        InspectionReportIntegrationEvidenceSource source = new();
        ExternalIntegrationEvidenceResult imported = await source.ReadAsync(
            report,
            new ExternalIntegrationImportContext(snapshot),
            options,
            cancellationToken).ConfigureAwait(false);

        if (imported.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            return new ExternalIntegrationSnapshotImportResult(snapshot, 0, imported.Diagnostics);
        }

        RepositorySnapshot merged = ExternalIntegrationEvidenceMerger.Merge(
            snapshot,
            imported.RepositoryEvidence);
        merged = merged with
        {
            Diagnostics =
            [
                .. merged.Diagnostics
                    .Concat(imported.Diagnostics.Select(item => new RepositoryDiagnostic(
                        item.Code,
                        item.Severity,
                        null,
                        item.Message)))
                    .Distinct()
                    .OrderBy(item => item.Code, StringComparer.Ordinal)
                    .ThenBy(item => item.RelativePath, StringComparer.Ordinal)
                    .ThenBy(item => item.Message, StringComparer.Ordinal),
            ],
            ExternalIntegrationEvidence = imported,
        };
        _ = ContractJson.SerializeSnapshot(merged);
        return new ExternalIntegrationSnapshotImportResult(
            merged,
            imported.Evidence.Length,
            imported.Diagnostics);
    }
}

/// <summary>Deterministically merges normalized external evidence without replacing local facts.</summary>
public static class ExternalIntegrationEvidenceMerger
{
    public static RepositorySnapshot Merge(
        RepositorySnapshot snapshot,
        ImmutableArray<Evidence> importedEvidence)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Dictionary<string, Evidence> evidence = snapshot.Evidence
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (Evidence imported in importedEvidence)
        {
            if (evidence.TryGetValue(imported.Id, out Evidence? existing) && existing != imported)
            {
                throw new InvalidOperationException("Imported external evidence conflicts with an existing evidence ID.");
            }

            evidence[imported.Id] = imported;
        }

        RepositorySnapshot result = snapshot with
        {
            Evidence = [.. evidence.Values.OrderBy(item => item.Id, StringComparer.Ordinal)],
        };
        _ = ContractJson.SerializeSnapshot(result);
        return result;
    }
}

/// <summary>Local snapshot and optional authoritative identity used to reject unrelated or stale reports.</summary>
public sealed record ExternalIntegrationImportContext(RepositorySnapshot Snapshot)
{
    public string? ExpectedRepositoryName
    {
        get; init;
    }

    public string? ExpectedCommitSha
    {
        get; init;
    }
}

/// <summary>Hard limits applied while reading an untrusted InspectionReport JSON stream.</summary>
public sealed record ExternalIntegrationEvidenceReadOptions
{
    public const long DefaultMaxJsonBytes = 8 * 1024 * 1024;

    public const int DefaultMaxFindings = 5_000;

    public const int DefaultMaxSignalsPerFinding = 32;

    public const int DefaultMaxTextLength = 512;

    public long MaxJsonBytes { get; init; } = DefaultMaxJsonBytes;

    public int MaxFindings { get; init; } = DefaultMaxFindings;

    public int MaxSignalsPerFinding { get; init; } = DefaultMaxSignalsPerFinding;

    public int MaxTextLength { get; init; } = DefaultMaxTextLength;
}

/// <summary>
/// Reads external integration findings through the public, versioned JSON boundary only.
/// Implementations must not execute DotNetRepoInspector or retain the input report.
/// </summary>
public interface IExternalIntegrationEvidenceSource
{
    ValueTask<ExternalIntegrationEvidenceResult> ReadAsync(
        Stream report,
        ExternalIntegrationImportContext context,
        ExternalIntegrationEvidenceReadOptions? options = null,
        CancellationToken cancellationToken = default);
}
