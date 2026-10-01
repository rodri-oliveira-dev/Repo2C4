using Microsoft.Extensions.AI;

namespace Repo2C4.Agent.Tests;

internal sealed class TestMcpSessionFactory(
    IReadOnlyList<AITool>? tools = null,
    string? diagnostic = null,
    IAgentMcpWriteGateway? writeGateway = null) : IAgentMcpSessionFactory
{
    public TestMcpSession Session
    {
        get;
    } = new(tools ?? [], writeGateway ?? new TestMcpWriteGateway());

    public AgentHostOptions? Options
    {
        get;
        private set;
    }

    public ValueTask<AgentMcpSessionCreation> CreateAsync(
        AgentHostOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Options = options;

        return ValueTask.FromResult(
            diagnostic is null
                ? AgentMcpSessionCreation.Success(Session)
                : AgentMcpSessionCreation.Failure(diagnostic));
    }
}

internal sealed class TestMcpSession(
    IReadOnlyList<AITool> tools,
    IAgentMcpWriteGateway writeGateway) : IAgentMcpSession
{
    public IReadOnlyList<AITool> Tools
    {
        get;
    } = tools;

    public AgentMcpInvocationState InvocationState
    {
        get;
    } = new();

    public IAgentMcpWriteGateway WriteGateway
    {
        get;
    } = writeGateway;

    public Task Completion => Task.CompletedTask;

    public bool IsDisposed
    {
        get;
        private set;
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestMcpWriteGateway : IAgentMcpWriteGateway
{
    public ValueTask<AgentWriteApprovalPlan> PrepareAsync(
        IReadOnlyList<AgentArchitectureProposal> proposals,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        _ = proposals;
        _ = destinationRoot;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new AgentWriteApprovalPlan(destinationRoot, []));
    }

    public ValueTask<AgentWriteApplyResult> ApplyAsync(
        AgentWriteApprovalPlan plan,
        CancellationToken cancellationToken)
    {
        _ = plan;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new AgentWriteApplyResult(
                AgentWriteApplyStatus.Failed,
                [],
                "Test gateway has no write plan."));
    }
}
