using Microsoft.Extensions.AI;
using Xunit;

namespace Repo2C4.Agent.Tests;

public sealed class AgentExecutionGovernanceTests
{
    [Fact]
    public void SafeBudgetOptionsAreParsed()
    {
        bool parsed = AgentHostOptions.TryParse(
            [
                "--provider", "ollama",
                "--model", "model",
                "--goal", "Document architecture",
                "--timeout-seconds", "60",
                "--max-duration-seconds", "120",
                "--max-tool-calls", "12",
                "--max-workflow-iterations", "3",
                "--max-evidence-pages", "8",
                "--max-response-chars", "12000",
                "--max-context-chars", "24000",
            ],
            out AgentHostOptions? options,
            out string? error);

        Assert.True(parsed, error);
        Assert.NotNull(options);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(120), options.MaxRunDuration);
        Assert.Equal(12, options.MaxToolCalls);
        Assert.Equal(3, options.MaxWorkflowIterations);
        Assert.Equal(8, options.MaxEvidencePages);
        Assert.Equal(12_000, options.MaxResponseCharacters);
        Assert.Equal(24_000, options.MaxContextCharacters);
    }

    [Theory]
    [InlineData("--max-duration-seconds", "0")]
    [InlineData("--max-tool-calls", "0")]
    [InlineData("--max-workflow-iterations", "4")]
    [InlineData("--max-evidence-pages", "51")]
    [InlineData("--max-response-chars", "100")]
    [InlineData("--max-context-chars", "100")]
    public void UnsafeBudgetOptionsAreRejected(string option, string value)
    {
        bool parsed = AgentHostOptions.TryParse(
            [
                "--provider", "ollama",
                "--model", "model",
                "--goal", "Document architecture",
                option, value,
            ],
            out _,
            out string? error);

        Assert.False(parsed);
        Assert.Contains(option, error, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolCallBudgetFailsInControlledWay()
    {
        AgentExecutionContext execution = CreateExecution(maxToolCalls: 1);

        using (AgentOperationScope first = execution.BeginTool("inspect_repository"))
        {
            first.Succeed();
        }

        AgentBudgetExceededException exception = Assert.Throws<AgentBudgetExceededException>(
            () => execution.BeginTool("validate_likec4"));

        Assert.Equal("tool_calls_exceeded", exception.Code);
        Assert.Equal(2, execution.SnapshotCounters().ToolCalls);
    }

    [Fact]
    public void WorkflowIterationBudgetFailsInControlledWay()
    {
        AgentExecutionContext execution = CreateExecution(maxWorkflowIterations: 1);

        using (AgentOperationScope first = execution.BeginWorkflowStage(
                   "analysis",
                   countsAsIteration: true))
        {
            first.Succeed();
        }

        AgentBudgetExceededException exception = Assert.Throws<AgentBudgetExceededException>(
            () => execution.BeginWorkflowStage("correction", countsAsIteration: true));

        Assert.Equal("workflow_iterations_exceeded", exception.Code);
        Assert.Equal(2, execution.SnapshotCounters().WorkflowIterations);
    }

    [Fact]
    public void EvidencePageBudgetFailsInControlledWay()
    {
        AgentExecutionContext execution = CreateExecution(maxEvidencePages: 1);

        using (AgentOperationScope first = execution.BeginTool("get_evidence"))
        {
            first.Succeed();
        }

        AgentBudgetExceededException exception = Assert.Throws<AgentBudgetExceededException>(
            () => execution.BeginTool("get_snapshot"));

        Assert.Equal("evidence_pages_exceeded", exception.Code);
        Assert.Equal(2, execution.SnapshotCounters().EvidencePages);
    }

    [Fact]
    public void ResponseAndContextBudgetsAreIndependentCumulativeAndBounded()
    {
        AgentExecutionContext responseExecution =
            CreateExecution(maxResponseCharacters: 8);
        responseExecution.ObserveResponse("12345");
        AgentBudgetExceededException responseException =
            Assert.Throws<AgentBudgetExceededException>(
                () => responseExecution.ObserveResponse("6789"));

        AgentExecutionContext contextExecution =
            CreateExecution(maxContextCharacters: 8);
        contextExecution.ObserveContext("12345");
        AgentBudgetExceededException contextException =
            Assert.Throws<AgentBudgetExceededException>(
                () => contextExecution.ObserveContext("6789"));

        Assert.Equal("response_size_exceeded", responseException.Code);
        Assert.Equal("context_size_exceeded", contextException.Code);
        Assert.Equal(9, responseExecution.SnapshotCounters().ResponseCharacters);
        Assert.Equal(9, contextExecution.SnapshotCounters().ContextCharacters);
    }

    [Fact]
    public async Task ProviderRequestCountsInstructionsToolsAndFunctionResults()
    {
        AgentExecutionContext execution = CreateExecution(maxContextCharacters: 40);
        using TestChatClient inner = new("unused");
        using BoundedProviderChatClient client = new(
            inner,
            "test",
            TimeSpan.FromSeconds(5),
            execution);
        AIFunction tool = AIFunctionFactory.Create(
            (string value) => value,
            "echo_tool",
            "A tool description that consumes context.");

        ChatMessage message = new(
            ChatRole.Tool,
            [
                new FunctionResultContent(
                    "call-1",
                    "A sufficiently large structured tool result."),
            ]);
        ChatOptions options = new()
        {
            Instructions = "System instructions also count.",
            Tools = [tool],
        };

        AgentBudgetExceededException exception =
            await Assert.ThrowsAsync<AgentBudgetExceededException>(
                () => client.GetResponseAsync([message], options));

        Assert.Equal("context_size_exceeded", exception.Code);
        Assert.True(execution.SnapshotCounters().ContextCharacters > 40);
    }

    [Fact]
    public async Task TotalDurationBudgetCancelsLinkedExecution()
    {
        AgentExecutionContext execution = new(
            new AgentExecutionBudgets(
                TimeSpan.FromMilliseconds(25),
                10,
                3,
                10,
                1_000,
                1_000));

        using CancellationTokenSource deadline =
            execution.CreateDeadlineSource(CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Task.Delay(TimeSpan.FromSeconds(2), deadline.Token));

        Assert.True(execution.IsDeadlineExceeded);
    }

    [Fact]
    public void ParentCancellationDoesNotMasqueradeAsDeadlineExceeded()
    {
        AgentExecutionContext execution = CreateExecution();
        using CancellationTokenSource parent = new();
        using CancellationTokenSource deadline = execution.CreateDeadlineSource(parent.Token);

        parent.Cancel();

        Assert.True(deadline.IsCancellationRequested);
        Assert.False(execution.IsDeadlineExceeded);
    }

    [Fact]
    public void StructuredLogsNeverContainObservedPromptOrResponseContent()
    {
        const string secret = "SECRET_PROMPT_AND_EVIDENCE_CONTENT";
        AgentExecutionContext execution = CreateExecution();
        using StringWriter writer = new();
        execution.ConfigureStructuredLogging(writer);

        execution.ObserveContext(secret);
        execution.ObserveResponse(secret);
        using (AgentOperationScope tool = execution.BeginTool("inspect_repository"))
        {
            tool.Fail("controlled_error");
        }

        execution.Complete("failed", "controlled_error");

        string log = writer.ToString();
        Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        Assert.Contains(execution.RunId, log, StringComparison.Ordinal);
        Assert.Contains("inspect_repository", log, StringComparison.Ordinal);
        Assert.Contains("controlled_error", log, StringComparison.Ordinal);
    }

    private static AgentExecutionContext CreateExecution(
        int maxToolCalls = 10,
        int maxWorkflowIterations = 3,
        int maxEvidencePages = 10,
        int maxResponseCharacters = 1_000,
        int maxContextCharacters = 1_000) =>
        new(
            new AgentExecutionBudgets(
                TimeSpan.FromSeconds(30),
                maxToolCalls,
                maxWorkflowIterations,
                maxEvidencePages,
                maxResponseCharacters,
                maxContextCharacters),
            runId: "test-run");
}
