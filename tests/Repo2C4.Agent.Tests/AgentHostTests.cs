using Microsoft.Extensions.AI;
using Xunit;

namespace Repo2C4.Agent.Tests;

public sealed class AgentHostTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task HelpUsesDiagnosticChannel(string argument)
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            [argument],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("--provider", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--model", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--allow-external-ai", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("OPENAI_API_KEY", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "--model", "model" }, "--provider")]
    [InlineData(new[] { "--provider", "ollama" }, "--model")]
    [InlineData(new[] { "--provider", "", "--model", "model" }, "--provider")]
    [InlineData(new[] { "--provider", "unsupported", "--model", "model" }, "supported providers")]
    [InlineData(new[] { "--provider", "ollama", "--model", "bad model" }, "--model")]
    public async Task InvalidProviderOrModelFailsWithControlledDiagnostic(
        string[] args,
        string expectedText)
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            args,
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains(expectedText, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedArgumentDoesNotEchoPotentialSecret()
    {
        const string secret = "SECRET_VALUE_MUST_NOT_BE_LOGGED";
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--api-key", secret, "--provider", "openai", "--model", "unit-model", "--allow-external-ai"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("unsupported argument", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OllamaConfigurationOnlyCreatesProviderWithoutNetwork()
    {
        using TestChatClient chatClient = new("unused");
        RecordingChatClientFactory chatClientFactory = new(chatClient);
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--provider", "ollama", "--model", "explicit-model"],
            output,
            error,
            TestContext.Current.CancellationToken,
            chatClientFactory);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
        Assert.NotNull(chatClientFactory.Options);
        Assert.Equal(AgentHostOptions.DefaultOllamaEndpoint, chatClientFactory.Options.Endpoint);
    }

    [Fact]
    public async Task PromptExecutionUsesInjectedChatClientAndPreservesExplicitConfiguration()
    {
        using TestChatClient chatClient = new("agent-response");
        RecordingChatClientFactory chatClientFactory = new(chatClient);
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            [
                "--provider", "ollama",
                "--model", "fake-model",
                "--endpoint", "http://localhost:11435/",
                "--timeout-seconds", "17",
                "--prompt", "hello",
            ],
            output,
            error,
            TestContext.Current.CancellationToken,
            chatClientFactory);

        Assert.Equal(0, exitCode);
        Assert.Equal("agent-response" + Environment.NewLine, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
        Assert.NotNull(chatClientFactory.Options);
        Assert.Equal("ollama", chatClientFactory.Options.Provider);
        Assert.Equal("fake-model", chatClientFactory.Options.Model);
        Assert.Equal(new Uri("http://localhost:11435/"), chatClientFactory.Options.Endpoint);
        Assert.Equal(TimeSpan.FromSeconds(17), chatClientFactory.Options.Timeout);
    }

    [Theory]
    [InlineData("https://remote.example/")]
    [InlineData("http://remote.example:11434/")]
    [InlineData("http://user:pass@127.0.0.1:11434/")]
    [InlineData("http://127.0.0.1:11434/api/chat")]
    public async Task OllamaRejectsNonLocalOrNonOriginEndpoint(string endpoint)
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--provider", "ollama", "--model", "model", "--endpoint", endpoint],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("HTTP loopback origin", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiRequiresExplicitConsentBeforeProviderCreation()
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--provider", "openai", "--model", "model"],
            output,
            error,
            TestContext.Current.CancellationToken,
            new ThrowingChatClientFactory());

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("--allow-external-ai", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiRejectsEndpointOption()
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            [
                "--provider", "openai",
                "--model", "model",
                "--allow-external-ai",
                "--endpoint", "http://127.0.0.1:11434/",
            ],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Contains("--endpoint applies only to ollama", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("301")]
    [InlineData("not-a-number")]
    public async Task InvalidTimeoutIsRejected(string timeout)
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--provider", "ollama", "--model", "model", "--timeout-seconds", timeout],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Contains("between 1 and 300", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class RecordingChatClientFactory(IChatClient chatClient) : IAgentChatClientFactory
    {
        public AgentHostOptions? Options
        {
            get;
            private set;
        }

        public ValueTask<AgentChatClientCreation> CreateAsync(
            AgentHostOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Options = options;
            return ValueTask.FromResult(AgentChatClientCreation.Success(chatClient));
        }
    }

    private sealed class ThrowingChatClientFactory : IAgentChatClientFactory
    {
        public ValueTask<AgentChatClientCreation> CreateAsync(
            AgentHostOptions options,
            CancellationToken cancellationToken)
        {
            _ = options;
            _ = cancellationToken;
            throw new InvalidOperationException("Factory must not be invoked.");
        }
    }
}
