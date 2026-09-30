using Microsoft.Extensions.AI;
using Xunit;

namespace Repo2C4.Agent.Tests;

public sealed class AgentProviderTests
{
    [Fact]
    public async Task OllamaAndOpenAiUseSameFactoryContractAndConfiguredModel()
    {
        List<(string Provider, string Model)> created = [];
        ProviderAgentChatClientFactory factory = new(
            createOllamaClient: (_, model) =>
            {
                created.Add(("ollama", model));
                return new TestChatClient("ollama-response");
            },
            createOpenAiClient: (model, _) =>
            {
                created.Add(("openai", model));
                return new TestChatClient("openai-response");
            },
            getEnvironmentVariable: _ => "environment-secret");

        (int ollamaExit, string ollamaOutput, string ollamaError) = await RunAsync(
            ["--provider", "ollama", "--model", "local-model", "--prompt", "hello"],
            factory);
        (int openAiExit, string openAiOutput, string openAiError) = await RunAsync(
            [
                "--provider", "openai",
                "--model", "cloud-model",
                "--allow-external-ai",
                "--prompt", "hello",
            ],
            factory);

        Assert.Equal(0, ollamaExit);
        Assert.Equal(0, openAiExit);
        Assert.Equal("ollama-response" + Environment.NewLine, ollamaOutput);
        Assert.Equal("openai-response" + Environment.NewLine, openAiOutput);
        Assert.Equal(string.Empty, ollamaError);
        Assert.Equal(string.Empty, openAiError);
        Assert.Contains(("ollama", "local-model"), created);
        Assert.Contains(("openai", "cloud-model"), created);
    }

    [Fact]
    public async Task OpenAiReadsCredentialFromEnvironmentOnlyAndNeverPrintsIt()
    {
        const string secret = "sk-SECRET_VALUE_MUST_NOT_BE_LOGGED";
        string? receivedSecret = null;
        ProviderAgentChatClientFactory factory = new(
            createOpenAiClient: (_, apiKey) =>
            {
                receivedSecret = apiKey;
                return new TestChatClient("ok");
            },
            getEnvironmentVariable: name =>
                name == AgentHostOptions.OpenAiApiKeyEnvironmentVariable ? secret : null);

        (int exitCode, string output, string error) = await RunAsync(
            [
                "--provider", "openai",
                "--model", "cloud-model",
                "--allow-external-ai",
                "--prompt", "hello",
            ],
            factory);

        Assert.Equal(0, exitCode);
        Assert.Equal(secret, receivedSecret);
        Assert.Equal("ok" + Environment.NewLine, output);
        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiMissingEnvironmentCredentialFailsBeforeClientCreation()
    {
        int creations = 0;
        ProviderAgentChatClientFactory factory = new(
            createOpenAiClient: (_, _) =>
            {
                creations++;
                return new TestChatClient("unused");
            },
            getEnvironmentVariable: _ => null);

        (int exitCode, string output, string error) = await RunAsync(
            [
                "--provider", "openai",
                "--model", "cloud-model",
                "--allow-external-ai",
            ],
            factory);

        Assert.Equal(2, exitCode);
        Assert.Equal(0, creations);
        Assert.Equal(string.Empty, output);
        Assert.Contains("OPENAI_API_KEY", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OllamaUsesLoopbackDefaultAndConfiguredEndpoint()
    {
        List<Uri> endpoints = [];
        ProviderAgentChatClientFactory factory = new(
            createOllamaClient: (endpoint, _) =>
            {
                endpoints.Add(endpoint);
                return new TestChatClient("unused");
            });

        (int firstExit, _, _) = await RunAsync(
            ["--provider", "ollama", "--model", "local"],
            factory);
        (int secondExit, _, _) = await RunAsync(
            [
                "--provider", "ollama",
                "--model", "local",
                "--endpoint", "http://localhost:11435/",
            ],
            factory);

        Assert.Equal(0, firstExit);
        Assert.Equal(0, secondExit);
        Assert.Equal(
            [AgentHostOptions.DefaultOllamaEndpoint, new Uri("http://localhost:11435/")],
            endpoints);
    }

    [Fact]
    public async Task ProviderHttpErrorIsSanitized()
    {
        const string providerDetail = "PRIVATE_SERVER_BODY_OR_SECRET";
        ProviderAgentChatClientFactory factory = new(
            createOllamaClient: (_, _) => new DelegatingTestChatClient(
                (_, _, _) => throw new HttpRequestException(providerDetail)));

        (int exitCode, string output, string error) = await RunAsync(
            ["--provider", "ollama", "--model", "local", "--prompt", "hello"],
            factory);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Contains("Ollama request failed.", error, StringComparison.Ordinal);
        Assert.DoesNotContain(providerDetail, error, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderTimeoutIsControlled()
    {
        ProviderAgentChatClientFactory factory = new(
            createOllamaClient: (_, _) => new DelegatingTestChatClient(
                async (_, _, token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException("Unreachable.");
                }));

        (int exitCode, string output, string error) = await RunAsync(
            [
                "--provider", "ollama",
                "--model", "local",
                "--timeout-seconds", "1",
                "--prompt", "hello",
            ],
            factory);

        Assert.Equal(1, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Contains("Ollama request timed out.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsProviderTimeout()
    {
        using CancellationTokenSource cancellation = new();
        ProviderAgentChatClientFactory factory = new(
            createOllamaClient: (_, _) => new DelegatingTestChatClient(
                (_, _, token) =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<ChatResponse>(token);
                }));

        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = await Program.RunAsync(
            ["--provider", "ollama", "--model", "local", "--prompt", "hello"],
            output,
            error,
            cancellation.Token,
            factory);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.DoesNotContain("timed out", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string[] args,
        IAgentChatClientFactory factory)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = await Program.RunAsync(
            args,
            output,
            error,
            TestContext.Current.CancellationToken,
            factory);
        return (exitCode, output.ToString(), error.ToString());
    }

    private sealed class DelegatingTestChatClient(
        Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> response)
        : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            response(chatMessages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Empty(cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            _ = serviceKey;
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> Empty(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
