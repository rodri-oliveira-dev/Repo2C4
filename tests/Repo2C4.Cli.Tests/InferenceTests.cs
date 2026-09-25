using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Repo2C4.Cli.Inference;
using Repo2C4.Core.Contracts;
using Xunit;

namespace Repo2C4.Cli.Tests;

public sealed class InferenceTests
{
    private static readonly string SnapshotPath =
        Path.Combine(AppContext.BaseDirectory, "EndToEnd", "snapshot.v1.json");

    [Fact]
    public async Task InferProducesValidatedReviewRequiredCandidateWithoutSourceOrSecretsOnWire()
    {
        RepositorySnapshot snapshot = ContractJson.DeserializeSnapshot(File.ReadAllText(SnapshotPath));
        string sensitivePath = "private/customer-api-token.txt";
        snapshot = snapshot with
        {
            Files = [snapshot.Files[0] with { RelativePath = sensitivePath }],
            Evidence =
            [
                .. snapshot.Evidence.Select(item => item with
                {
                    RelativePath = sensitivePath,
                    Description = item.Description + " SECRET_VALUE_DO_NOT_SEND",
                }),
            ],
            Diagnostics =
            [
                new RepositoryDiagnostic("sensitive", DiagnosticSeverity.Warning, sensitivePath, "SECRET_VALUE_DO_NOT_SEND"),
            ],
        };

        using TempFolder temp = new();
        string input = Path.Combine(temp.Path, "snapshot.json");
        string output = Path.Combine(temp.Path, "candidate.json");
        File.WriteAllText(input, ContractJson.SerializeSnapshot(snapshot));

        string? transmitted = null;
        using HttpClient client = CreateClient(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("http://127.0.0.1:11434/api/generate", request.RequestUri!.ToString());
            transmitted = await request.Content!.ReadAsStringAsync(token);
            string proposal = ValidProposal(OpaqueEvidenceId(snapshot.Evidence[0].Id));
            return Success(proposal);
        });

        (int exit, string stdout, string stderr) = await RunAsync(
            ["infer", "--snapshot", input, "--provider", "ollama", "--model-id", "local-model:latest", "--output", output],
            client);

        Assert.Equal(CliExitCodes.Success, exit);
        Assert.Equal(output + Environment.NewLine, stdout);
        Assert.Contains("provider=ollama model=local-model:latest", stderr, StringComparison.Ordinal);
        Assert.NotNull(transmitted);
        Assert.DoesNotContain("SECRET_VALUE_DO_NOT_SEND", transmitted, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitivePath, transmitted, StringComparison.Ordinal);
        Assert.DoesNotContain(snapshot.RepositoryId, transmitted, StringComparison.Ordinal);
        Assert.DoesNotContain(snapshot.Evidence[0].Id, transmitted, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", transmitted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("files/file_0001", transmitted, StringComparison.Ordinal);

        ArchitectureModel candidate = ContractJson.DeserializeModel(File.ReadAllText(output));
        Assert.Equal(snapshot.RepositoryId, candidate.Snapshot.RepositoryId);
        Assert.Equal(sensitivePath, candidate.Snapshot.Files[0].RelativePath);
        Assert.Single(candidate.Elements);
        Assert.Equal(ReviewStatus.RequiresReview, candidate.Elements[0].Status);
        Assert.Contains("AI-generated", candidate.Elements[0].ReviewReason!, StringComparison.Ordinal);
        Assert.Equal(snapshot.Evidence[0].Id, candidate.Elements[0].EvidenceIds[0]);
        Assert.Empty(candidate.Relations);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "likec4")));
    }

    [Fact]
    public async Task LocalFixtureCandidateSupportsReviewedGenerateAndOptionalOfficialValidation()
    {
        RepositorySnapshot snapshot = ContractJson.DeserializeSnapshot(File.ReadAllText(SnapshotPath));
        using TempFolder temp = new();
        using HttpClient client = CreateClient((_, _) =>
            Task.FromResult(Success(ValidProposal(OpaqueEvidenceId(snapshot.Evidence[0].Id)))));
        string candidate = Path.Combine(temp.Path, "candidate.json");
        string generated = Path.Combine(temp.Path, "likec4");

        (int inferExit, _, _) = await RunAsync(
            ["infer", "--snapshot", SnapshotPath, "--provider", "ollama", "--model-id", "local", "--output", candidate],
            client);

        Assert.Equal(CliExitCodes.Success, inferExit);
        ArchitectureModel proposed = ContractJson.DeserializeModel(File.ReadAllText(candidate));
        Assert.All(proposed.Elements, element => Assert.Equal(ReviewStatus.RequiresReview, element.Status));

        // Inference does not apply architectural decisions. The fixture's system proposal is
        // explicitly reviewed before applying LikeC4 output, and no source file is changed.
        int generateExit = Program.Run(
            ["generate", "--model", candidate, "--output", generated, "--apply"],
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(CliExitCodes.Success, generateExit);
        Assert.True(File.Exists(Path.Combine(generated, "model.c4")));
        Assert.True(File.Exists(Path.Combine(generated, "evidence-report.md")));

        if (string.Equals(Environment.GetEnvironmentVariable("REPO2C4_LIKEC4_INTEGRATION"), "1", StringComparison.Ordinal))
        {
            int validateExit = Program.Run(
                ["validate", "--output", generated],
                TextWriter.Null,
                TextWriter.Null);
            Assert.Equal(CliExitCodes.Success, validateExit);
        }
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"level\":\"C1\",\"elements\":[{\"id\":\"INVALID\"}],\"relations\":[]}")]
    public async Task InvalidProviderProposalIsRejectedWithoutCandidateFile(string proposal)
    {
        using TempFolder temp = new();
        string output = Path.Combine(temp.Path, "candidate.json");
        using HttpClient client = CreateClient((_, _) => Task.FromResult(Success(proposal)));

        (int exit, _, string error) = await RunAsync(
            ["infer", "--snapshot", SnapshotPath, "--provider", "ollama", "--model-id", "local", "--output", output],
            client);

        Assert.Equal(CliExitCodes.InvalidData, exit);
        Assert.False(File.Exists(output));
        Assert.Contains("proposal", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingLocalModelIsReportedWithoutResponseBody()
    {
        using TempFolder temp = new();
        using HttpClient client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("PRIVATE_SERVER_ERROR_TOKEN"),
        }));

        (int exit, _, string error) = await RunAsync(
            ["infer", "--snapshot", SnapshotPath, "--provider", "ollama", "--model-id", "missing", "--output", Path.Combine(temp.Path, "candidate.json")],
            client);

        Assert.Equal(CliExitCodes.InferenceFailed, exit);
        Assert.Contains("unavailable", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE_SERVER_ERROR_TOKEN", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedWithoutCandidateFile()
    {
        using TempFolder temp = new();
        using HttpClient client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[(256 * 1024) + 1]),
        }));
        string output = Path.Combine(temp.Path, "candidate.json");

        (int exit, _, _) = await RunAsync(
            ["infer", "--snapshot", SnapshotPath, "--provider", "ollama", "--model-id", "local", "--output", output],
            client);

        Assert.Equal(CliExitCodes.InvalidData, exit);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task TimeoutStopsTransportAndDoesNotCreateCandidate()
    {
        using TempFolder temp = new();
        using HttpClient client = CreateClient(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable.");
        });
        string output = Path.Combine(temp.Path, "candidate.json");

        (int exit, _, string error) = await RunAsync(
            ["infer", "--snapshot", SnapshotPath, "--provider", "ollama", "--model-id", "local", "--output", output, "--timeout-seconds", "1"],
            client);

        Assert.Equal(CliExitCodes.InferenceFailed, exit);
        Assert.Contains("timed out", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CallerCancellationPropagatesWithoutCandidate()
    {
        using TempFolder temp = new();
        using CancellationTokenSource cancellation = new();
        using HttpClient client = CreateClient((_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        string output = Path.Combine(temp.Path, "candidate.json");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Program.RunAsync(
            ["infer", "--snapshot", SnapshotPath, "--provider", "ollama", "--model-id", "local", "--output", output],
            new StringWriter(),
            new StringWriter(),
            cancellation.Token,
            client));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task RejectsNonLoopbackEndpointsAndDoesNotContactTransport()
    {
        using TempFolder temp = new();
        using HttpClient client = CreateClient((_, _) => throw new InvalidOperationException("No external request permitted."));

        (int exit, _, string error) = await RunAsync(
            ["infer", "--snapshot", SnapshotPath, "--provider", "ollama", "--model-id", "local", "--endpoint", "https://remote.example/", "--output", Path.Combine(temp.Path, "candidate.json")],
            client);

        Assert.Equal(CliExitCodes.UsageError, exit);
        Assert.Contains("Invalid inference option", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExistingCandidateIsNotOverwrittenOrInferredAgain()
    {
        using TempFolder temp = new();
        string output = Path.Combine(temp.Path, "candidate.json");
        File.WriteAllText(output, "manual review");
        using HttpClient client = CreateClient((_, _) => throw new InvalidOperationException("Should not call Ollama."));

        (int exit, _, _) = await RunAsync(
            ["infer", "--snapshot", SnapshotPath, "--provider", "ollama", "--model-id", "local", "--output", output],
            client);

        Assert.Equal(CliExitCodes.IoError, exit);
        Assert.Equal("manual review", File.ReadAllText(output));
    }

    [Fact]
    public async Task ProviderCannotReplaceSnapshotOrInventEvidence()
    {
        RepositorySnapshot snapshot = ContractJson.DeserializeSnapshot(File.ReadAllText(SnapshotPath));
        IArchitectureInferenceProvider provider = new AlteredSnapshotProvider();
        await Assert.ThrowsAsync<InferenceException>(() => ArchitectureInference.ProposeAsync(snapshot, provider, CancellationToken.None));
    }

    private static string OpaqueEvidenceId(string id) =>
        "ev_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant()[..24];

    private static string ValidProposal(string evidenceId) => JsonSerializer.Serialize(new
    {
        schemaVersion = "1.0",
        level = "C1",
        elements = new[]
        {
            new
            {
                id = "el_proposed",
                kind = "softwareSystem",
                name = "Proposed focal system",
                evidenceIds = new[] { evidenceId },
                status = "confirmed",
            },
        },
        relations = Array.Empty<object>(),
    });

    private static HttpResponseMessage Success(string proposal) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                response = proposal,
                done = true,
            }),
            Encoding.UTF8,
            "application/json"),
    };

    private static HttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new StubHandler(send));

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string[] args, HttpClient client)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exit = await Program.RunAsync(args, output, error, CancellationToken.None, client);
        return (exit, output.ToString(), error.ToString());
    }

    private sealed class AlteredSnapshotProvider : IArchitectureInferenceProvider
    {
        public Task<ArchitectureModel> InferAsync(RepositorySnapshot sanitizedSnapshot, CancellationToken cancellationToken)
        {
            ArchitectureModel model = new(
                ContractSchema.Version,
                ArchitectureLevel.C1,
                sanitizedSnapshot with { RepositoryId = "repo_modified" },
                [],
                []);
            return Task.FromResult(model);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder() => Path = Directory.CreateTempSubdirectory("repo2c4-infer-").FullName;

        public string Path
        {
            get;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temporary test cleanup.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temporary test cleanup.
            }
        }
    }
}
