using System.Reflection;
using Microsoft.Extensions.AI;
using OllamaSharp;
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
        Assert.Contains("Status: completed", ollamaOutput, StringComparison.Ordinal);
        Assert.Contains("ollama-response", ollamaOutput, StringComparison.Ordinal);
        Assert.Contains("Status: completed", openAiOutput, StringComparison.Ordinal);
        Assert.Contains("openai-response", openAiOutput, StringComparison.Ordinal);
        Assert.Contains("\"runId\"", ollamaError, StringComparison.Ordinal);
        Assert.Contains("\"runId\"", openAiError, StringComparison.Ordinal);
        Assert.DoesNotContain("hello", ollamaError, StringComparison.Ordinal);
        Assert.DoesNotContain("hello", openAiError, StringComparison.Ordinal);
        Assert.DoesNotContain("environment-secret", openAiError, StringComparison.Ordinal);
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
        Assert.Contains("Status: completed", output, StringComparison.Ordinal);
        Assert.Contains("ok", output, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiMissingEnvironmentCredentialFailsBeforeMcpSession()
    {
        int creations = 0;
        ProviderAgentChatClientFactory factory = new(
            createOpenAiClient: (_, _) =>
            {
                creations++;
                return new TestChatClient("unused");
            },
            getEnvironmentVariable: _ => null);

        using StringWriter output = new();
        using StringWriter error = new();
        int exitCode = await Program.RunAsync(
            [
                "--provider", "openai",
                "--model", "cloud-model",
                "--allow-external-ai",
            ],
            output,
            error,
            TestContext.Current.CancellationToken,
            factory,
            mcpSessionFactory: new ThrowingMcpSessionFactory());

        Assert.Equal(2, exitCode);
        Assert.Equal(0, creations);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("OPENAI_API_KEY", error.ToString(), StringComparison.Ordinal);
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
    public async Task OllamaTransportHasNoIndependentHttpTimeout()
    {
        ProviderAgentChatClientFactory factory = new();
        AgentHostOptions options = new("ollama", "local-model", null)
        {
            Endpoint = new Uri("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromSeconds(180),
        };

        AgentChatClientCreation creation = await factory.CreateAsync(
            options,
            TestContext.Current.CancellationToken);

        using IChatClient chatClient = Assert.IsAssignableFrom<IChatClient>(creation.ChatClient);
        OllamaApiClient ollama = Assert.IsType<OllamaApiClient>(
            chatClient.GetService(typeof(OllamaApiClient)));
        FieldInfo transportField = typeof(OllamaApiClient).GetField(
            "_client",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Ollama transport field is unavailable.");
        HttpClient transport = Assert.IsType<HttpClient>(transportField.GetValue(ollama));

        Assert.Equal(Timeout.InfiniteTimeSpan, transport.Timeout);
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
            factory,
            mcpSessionFactory: new TestMcpSessionFactory(),
            workflowRunner: new DirectSessionWorkflowRunner());

        Assert.Equal(0, exitCode);
        Assert.Contains("Status: cancelled", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Terminal reason: cancelled", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Run ID:", output.ToString(), StringComparison.Ordinal);
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
            factory,
            mcpSessionFactory: new TestMcpSessionFactory(),
            workflowRunner: new DirectSessionWorkflowRunner());
        return (exitCode, output.ToString(), error.ToString());
    }

    private sealed class ThrowingMcpSessionFactory : IAgentMcpSessionFactory
    {
        public ValueTask<AgentMcpSessionCreation> CreateAsync(
            AgentHostOptions options,
            CancellationToken cancellationToken)
        {
            _ = options;
            _ = cancellationToken;
            throw new InvalidOperationException("MCP session must not be created.");
        }
    }

    private sealed class DirectSessionWorkflowRunner : IArchitectureAnalysisWorkflowRunner
    {
        private readonly AgentSessionRunner runner = new();

        public async Task<ArchitectureWorkflowResult> RunAsync(
            Microsoft.Agents.AI.AIAgent agent,
            AgentHostOptions options,
            IAgentMcpSession mcpSession,
            CancellationToken cancellationToken)
        {
            _ = mcpSession;
            string response = await runner
                .RunAsync(agent, options.Goal!, cancellationToken)
                .ConfigureAwait(false);

            return new ArchitectureWorkflowResult(
                ArchitectureWorkflowStatus.Completed,
                1,
                response,
                []);
        }
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
