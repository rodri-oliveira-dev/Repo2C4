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
    }

    [Theory]
    [InlineData(new[] { "--model", "model" }, "--provider")]
    [InlineData(new[] { "--provider", "provider" }, "--model")]
    [InlineData(new[] { "--provider", "", "--model", "model" }, "--provider")]
    public async Task MissingProviderOrModelFailsWithControlledDiagnostic(
        string[] args,
        string expectedOption)
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
        Assert.Contains(expectedOption, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedArgumentDoesNotEchoPotentialSecret()
    {
        const string secret = "SECRET_VALUE_MUST_NOT_BE_LOGGED";
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--api-key", secret, "--provider", "fake", "--model", "unit-model"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("unsupported argument", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderAndModelOnlyStartWithoutNetworkOrProviderAdapter()
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--provider", "future-provider", "--model", "explicit-model"],
            output,
            error,
            TestContext.Current.CancellationToken,
            new ThrowingChatClientFactory());

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task PromptExecutionUsesInjectedChatClientAndPreservesExplicitConfiguration()
    {
        using TestChatClient chatClient = new("agent-response");
        RecordingChatClientFactory chatClientFactory = new(chatClient);
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--provider", "fake-provider", "--model", "fake-model", "--prompt", "hello"],
            output,
            error,
            TestContext.Current.CancellationToken,
            chatClientFactory);

        Assert.Equal(0, exitCode);
        Assert.Equal("agent-response" + Environment.NewLine, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
        Assert.NotNull(chatClientFactory.Options);
        Assert.Equal("fake-provider", chatClientFactory.Options.Provider);
        Assert.Equal("fake-model", chatClientFactory.Options.Model);
    }

    [Fact]
    public async Task PromptWithoutRegisteredProviderFailsWithoutStackTraceOrConfigurationEcho()
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--provider", "provider-value", "--model", "model-value", "--prompt", "hello"],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("no provider adapter", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider-value", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("model-value", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", error.ToString(), StringComparison.Ordinal);
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
            throw new InvalidOperationException("Factory must not be invoked for configuration-only startup.");
        }
    }
}
