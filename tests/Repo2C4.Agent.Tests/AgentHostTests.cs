using Microsoft.Agents.AI;
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
        Assert.Contains("--repository-root", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--mcp-server-path", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--allow-external-ai", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "--model", "model" }, "--provider")]
    [InlineData(new[] { "--provider", "ollama" }, "--model")]
    [InlineData(new[] { "--provider", "unsupported", "--model", "model" }, "supported providers")]
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
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigurationOnlyInitializesProviderAndMcpCapabilitiesWithoutAnalysis()
    {
        using TestChatClient chatClient = new("unused");
        TestMcpSessionFactory mcpFactory = new();
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--provider", "ollama", "--model", "explicit-model"],
            output,
            error,
            TestContext.Current.CancellationToken,
            new RecordingChatClientFactory(chatClient),
            mcpSessionFactory: mcpFactory);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Equal(string.Empty, error.ToString());
        Assert.True(mcpFactory.Session.IsDisposed);
    }

    [Fact]
    public async Task MissingRepositoryRootFailsBeforeAnalysis()
    {
        using TestChatClient chatClient = new("unused");
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--provider", "ollama", "--model", "model"],
            output,
            error,
            TestContext.Current.CancellationToken,
            new RecordingChatClientFactory(chatClient));

        Assert.Equal(2, exitCode);
        Assert.Contains("--repository-root", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpCapabilityFailurePreventsAgentCreation()
    {
        using TestChatClient chatClient = new("unused");
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--provider", "ollama", "--model", "model"],
            output,
            error,
            TestContext.Current.CancellationToken,
            new RecordingChatClientFactory(chatClient),
            new ThrowingAgentFactory(),
            mcpSessionFactory: new TestMcpSessionFactory(
                diagnostic: "Repo2C4 Agent MCP capability error: required tools are unavailable: validate_likec4."));

        Assert.Equal(2, exitCode);
        Assert.Contains("required tools are unavailable", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationDisposesMcpSession()
    {
        using CancellationTokenSource cancellation = new();
        using TestChatClient chatClient = new("unused");
        TestMcpSessionFactory mcpFactory = new();
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--provider", "ollama", "--model", "model", "--prompt", "hello"],
            output,
            error,
            cancellation.Token,
            new RecordingChatClientFactory(chatClient),
            mcpSessionFactory: mcpFactory,
            workflowRunner: new CancelingWorkflowRunner(cancellation));

        Assert.Equal(0, exitCode);
        Assert.True(mcpFactory.Session.IsDisposed);
    }

    [Theory]
    [InlineData("https://remote.example/")]
    [InlineData("http://remote.example:11434/")]
    public async Task OllamaRejectsNonLocalEndpoint(string endpoint)
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--provider", "ollama", "--model", "model", "--endpoint", endpoint],
            output,
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Contains("HTTP loopback origin", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(endpoint, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ArchitectureWorkflowStatus.Failed)]
    [InlineData(ArchitectureWorkflowStatus.ValidationFailed)]
    public async Task FailedWorkflowWithWriteDestinationReturnsFailureBeforeApproval(
        ArchitectureWorkflowStatus status)
    {
        using TestChatClient chatClient = new("unused");
        TestMcpSessionFactory mcpFactory = new();
        RecordingWriteApprovalRunner approvalRunner = new();
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            [
                "--provider", "ollama",
                "--model", "model",
                "--goal", "Document architecture",
                "--write-destination", "architecture",
            ],
            output,
            error,
            TestContext.Current.CancellationToken,
            new RecordingChatClientFactory(chatClient),
            mcpSessionFactory: mcpFactory,
            workflowRunner: new FixedWorkflowRunner(status),
            writeApprovalRunner: approvalRunner);

        Assert.Equal(1, exitCode);
        Assert.Equal(0, approvalRunner.Calls);
        Assert.Contains("Status:", output.ToString(), StringComparison.Ordinal);
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
        Assert.Contains("--allow-external-ai", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class RecordingChatClientFactory(IChatClient chatClient) : IAgentChatClientFactory
    {
        public ValueTask<AgentChatClientCreation> CreateAsync(
            AgentHostOptions options,
            CancellationToken cancellationToken)
        {
            _ = options;
            cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class ThrowingAgentFactory : IRepo2C4AgentFactory
    {
        public AIAgent Create(
            IChatClient chatClient,
            AgentHostOptions options,
            IReadOnlyList<AITool> tools)
        {
            _ = chatClient;
            _ = options;
            _ = tools;
            throw new InvalidOperationException("Agent must not be created.");
        }
    }

    private sealed class CancelingWorkflowRunner(CancellationTokenSource cancellation)
        : IArchitectureAnalysisWorkflowRunner
    {
        public Task<ArchitectureWorkflowResult> RunAsync(
            AIAgent agent,
            AgentHostOptions options,
            IAgentMcpSession mcpSession,
            CancellationToken cancellationToken)
        {
            _ = agent;
            _ = options;
            _ = mcpSession;
            cancellation.Cancel();
            return Task.FromCanceled<ArchitectureWorkflowResult>(cancellationToken);
        }
    }

    private sealed class FixedWorkflowRunner(ArchitectureWorkflowStatus status)
        : IArchitectureAnalysisWorkflowRunner
    {
        public Task<ArchitectureWorkflowResult> RunAsync(
            AIAgent agent,
            AgentHostOptions options,
            IAgentMcpSession mcpSession,
            CancellationToken cancellationToken)
        {
            _ = agent;
            _ = options;
            _ = mcpSession;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new ArchitectureWorkflowResult(
                    status,
                    1,
                    "controlled result",
                    ["controlled diagnostic"]));
        }
    }

    private sealed class RecordingWriteApprovalRunner : IWriteApprovalRunner
    {
        public int Calls
        {
            get;
            private set;
        }

        public Task<WriteApprovalResult> RunAsync(
            IChatClient chatClient,
            AgentHostOptions options,
            IAgentMcpSession mcpSession,
            ArchitectureWorkflowResult workflowResult,
            CancellationToken cancellationToken)
        {
            _ = chatClient;
            _ = options;
            _ = mcpSession;
            _ = workflowResult;
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(
                new WriteApprovalResult(
                    WriteApprovalStatus.Denied,
                    [],
                    "unused"));
        }
    }

}
