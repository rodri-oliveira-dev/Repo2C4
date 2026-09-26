using System.Net;
using System.Text;
using System.Text.Json;
using Repo2C4.Cli.Inference;
using Repo2C4.Core.Contracts;
using Xunit;

namespace Repo2C4.Cli.Tests;

public sealed class OpenAiInferenceTests
{
    private const string TestKey = "sk-example-secret-never-log-987654";
    private const string ModelId = "test-model_2026-09-25";
    private static readonly string SnapshotPath = Path.Combine(AppContext.BaseDirectory, "EndToEnd", "snapshot.v1.json");

    [Fact]
    public async Task CloudConsentAndHostSecretAreRequiredBeforeAnyRequest()
    {
        using TempFolder temp = new();
        int calls = 0;
        using HttpClient client = CreateClient((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("Request was not authorized.");
        });
        string output = Path.Combine(temp.Path, "candidate.json");
        string[] args = ["infer", "--snapshot", SnapshotPath, "--provider", "openai", "--model-id", ModelId, "--output", output];

        (int noConsent, _, string consentError) = await RunAsync(args, client);
        Assert.Equal(CliExitCodes.UsageError, noConsent);
        Assert.Contains("--allow-external-ai", consentError, StringComparison.Ordinal);
        Assert.Equal(0, calls);

        WithApiKey(null, () =>
        {
            (int withoutKey, _, string keyError) = RunAsync([.. args, "--allow-external-ai"], client).GetAwaiter().GetResult();
            Assert.Equal(CliExitCodes.UsageError, withoutKey);
            Assert.Contains("OPENAI_API_KEY", keyError, StringComparison.Ordinal);
            Assert.Equal(0, calls);
        });
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CloudProviderSendsOnlySanitizedProjectionAndNeverLogsTheKey()
    {
        RepositorySnapshot original = ContractJson.DeserializeSnapshot(File.ReadAllText(SnapshotPath));
        const string sensitivePath = "secrets/private-project-key.txt";
        const string secretValue = "FIXTURE_SECRET_VALUE_NOT_FOR_CLOUD";
        RepositorySnapshot snapshot = original with
        {
            Files = [original.Files[0] with { RelativePath = sensitivePath }],
            Evidence =
            [
                .. original.Evidence.Select(evidence => evidence with
                {
                    RelativePath = sensitivePath,
                    Description = evidence.Description + " " + secretValue,
                }),
            ],
            Diagnostics =
            [
                new RepositoryDiagnostic("scan.private", DiagnosticSeverity.Warning, sensitivePath, secretValue),
            ],
        };

        using TempFolder temp = new();
        string snapshotFile = Path.Combine(temp.Path, "snapshot.json");
        string output = Path.Combine(temp.Path, "candidate.json");
        File.WriteAllText(snapshotFile, ContractJson.SerializeSnapshot(snapshot));
        RepositorySnapshot expected = InferenceSnapshotSanitizer.Sanitize(snapshot);
        int calls = 0;
        using HttpClient client = CreateClient(async (request, cancellationToken) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(TestKey, request.Headers.Authorization.Parameter);
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain(TestKey, body, StringComparison.Ordinal);
            Assert.DoesNotContain(secretValue, body, StringComparison.Ordinal);
            Assert.DoesNotContain(sensitivePath, body, StringComparison.Ordinal);
            Assert.DoesNotContain(snapshot.RepositoryId, body, StringComparison.Ordinal);
            Assert.DoesNotContain(snapshot.Evidence[0].Id, body, StringComparison.Ordinal);
            using JsonDocument json = JsonDocument.Parse(body);
            Assert.Equal(ModelId, json.RootElement.GetProperty("model").GetString());
            Assert.False(json.RootElement.GetProperty("store").GetBoolean());
            Assert.Equal("json_object", json.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            Assert.Equal(2, json.RootElement.GetProperty("input").GetArrayLength());
            Assert.Contains(expected.Evidence[0].Id, body, StringComparison.Ordinal);
            return Success(ValidProposal(expected.Evidence[0].Id, confirmed: true));
        });

        (int exit, string stdout, string stderr) = await WithApiKeyAsync(TestKey, () => RunAsync(
            ["infer", "--snapshot", snapshotFile, "--provider", "openai", "--model-id", ModelId,
                "--output", output, "--allow-external-ai"],
            client));

        Assert.Equal(CliExitCodes.Success, exit);
        Assert.Equal(1, calls);
        Assert.Equal(output + Environment.NewLine, stdout);
        Assert.Contains("api.openai.com", stderr, StringComparison.Ordinal);
        Assert.Contains("1 anonymized file entries and 3 sanitized evidence records", stderr, StringComparison.Ordinal);
        Assert.Contains("provider=openai model=" + ModelId, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(TestKey, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitivePath, stderr, StringComparison.Ordinal);

        ArchitectureModel candidate = ContractJson.DeserializeModel(File.ReadAllText(output));
        Assert.Equal(snapshot.RepositoryId, candidate.Snapshot.RepositoryId);
        Assert.Equal(sensitivePath, candidate.Snapshot.Files[0].RelativePath);
        Assert.Single(candidate.Elements);
        Assert.Equal(ReviewStatus.RequiresReview, candidate.Elements[0].Status);
        Assert.Equal(snapshot.Evidence[0].Id, candidate.Elements[0].EvidenceIds[0]);
        Assert.False(File.Exists(Path.Combine(temp.Path, "model.c4")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    [InlineData(HttpStatusCode.TooManyRequests, "429")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    [InlineData(HttpStatusCode.BadGateway, "502")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "503")]
    public async Task HttpFailureIsBoundedAndDoesNotLeakProviderBody(HttpStatusCode status, string code)
    {
        RepositorySnapshot sanitized = SanitizedFixture();
        using HttpClient client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(TestKey + " PRIVATE_SERVER_BODY"),
        }));
        OpenAiInferenceProvider provider = new(client, ModelId, TestKey, TimeSpan.FromSeconds(5));

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(
            () => provider.InferAsync(sanitized, CancellationToken.None));

        Assert.Equal(InferenceFailure.Unavailable, exception.Failure);
        Assert.Contains(code, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(TestKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_SERVER_BODY", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":\"1.0\",\"level\":\"C1\",\"elements\":[{\"id\":\"bad\"}],\"relations\":[]}")]
    public async Task RejectsInvalidStructuredOutputWithoutEchoingResponse(string proposal)
    {
        using HttpClient client = CreateClient((_, _) => Task.FromResult(Success(proposal)));
        OpenAiInferenceProvider provider = new(client, ModelId, TestKey, TimeSpan.FromSeconds(5));

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(
            () => provider.InferAsync(SanitizedFixture(), CancellationToken.None));

        Assert.Equal(InferenceFailure.InvalidResponse, exception.Failure);
        Assert.DoesNotContain(proposal, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("failed")]
    public async Task IncompleteResponseIsRejected(string status)
    {
        using HttpClient client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(new { status, output = Array.Empty<object>() }),
        }));
        OpenAiInferenceProvider provider = new(client, ModelId, TestKey, TimeSpan.FromSeconds(5));

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(
            () => provider.InferAsync(SanitizedFixture(), CancellationToken.None));

        Assert.Equal(InferenceFailure.InvalidResponse, exception.Failure);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedEvenWithoutContentLength()
    {
        using HttpClient client = CreateClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[(256 * 1024) + 1])),
        }));
        OpenAiInferenceProvider provider = new(client, ModelId, TestKey, TimeSpan.FromSeconds(5));

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(
            () => provider.InferAsync(SanitizedFixture(), CancellationToken.None));

        Assert.Equal(InferenceFailure.PayloadTooLarge, exception.Failure);
    }

    [Fact]
    public async Task TimeoutDoesNotExposeSecret()
    {
        using HttpClient client = CreateClient(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        });
        OpenAiInferenceProvider provider = new(client, ModelId, TestKey, TimeSpan.FromSeconds(1));

        InferenceException exception = await Assert.ThrowsAsync<InferenceException>(
            () => provider.InferAsync(SanitizedFixture(), CancellationToken.None));

        Assert.Equal(InferenceFailure.TimedOut, exception.Failure);
        Assert.DoesNotContain(TestKey, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        using CancellationTokenSource source = new();
        using HttpClient client = CreateClient((_, cancellationToken) =>
        {
            source.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        });
        OpenAiInferenceProvider provider = new(client, ModelId, TestKey, TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.InferAsync(SanitizedFixture(), source.Token));
    }

    [Fact]
    public async Task OllamaCannotOptIntoExternalSendingOrConfigureCloudEndpoint()
    {
        using TempFolder temp = new();
        int calls = 0;
        using HttpClient client = CreateClient((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("Unexpected request.");
        });
        string output = Path.Combine(temp.Path, "candidate.json");
        (int localExit, _, _) = await RunAsync(
            ["infer", "--snapshot", SnapshotPath, "--provider", "ollama", "--model-id", "local",
                "--output", output, "--allow-external-ai"],
            client);
        (int cloudExit, _, _) = await RunAsync(
            ["infer", "--snapshot", SnapshotPath, "--provider", "openai", "--model-id", ModelId,
                "--output", output, "--allow-external-ai", "--endpoint", "https://attacker.invalid/"],
            client);
        Assert.Equal(CliExitCodes.UsageError, localExit);
        Assert.Equal(CliExitCodes.UsageError, cloudExit);
        Assert.Equal(0, calls);
    }

    private static RepositorySnapshot SanitizedFixture() =>
        InferenceSnapshotSanitizer.Sanitize(ContractJson.DeserializeSnapshot(File.ReadAllText(SnapshotPath)));

    private static string ValidProposal(string evidenceId, bool confirmed) => JsonSerializer.Serialize(new
    {
        schemaVersion = "1.0",
        level = "C1",
        elements = new[]
        {
            new
            {
                id = "el_candidate",
                kind = "softwareSystem",
                name = "Proposed system",
                evidenceIds = new[] { evidenceId },
                status = confirmed ? "confirmed" : "requiresReview",
                reviewReason = "Requires architectural review.",
            },
        },
        relations = Array.Empty<object>(),
    });

    private static HttpResponseMessage Success(string proposal) => new(HttpStatusCode.OK)
    {
        Content = JsonContent(new
        {
            status = "completed",
            output = new[]
            {
                new
                {
                    type = "message",
                    role = "assistant",
                    content = new[] { new { type = "output_text", text = proposal } },
                },
            },
        }),
    };

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static HttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new StubHandler(send));

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string[] args, HttpClient client)
    {
        using StringWriter stdout = new();
        using StringWriter stderr = new();
        int exit = await Program.RunAsync(args, stdout, stderr, CancellationToken.None, client);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private static void WithApiKey(string? value, Action action)
    {
        string? previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    private static async Task<T> WithApiKeyAsync<T>(string value, Func<Task<T>> action)
    {
        string? previous = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", value);
            return await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previous);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class TempFolder : IDisposable
    {
        public TempFolder() => Path = Directory.CreateTempSubdirectory("repo2c4-openai-").FullName;

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
                // Best effort cleanup for test-only temporary files.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort cleanup for test-only temporary files.
            }
        }
    }
}
