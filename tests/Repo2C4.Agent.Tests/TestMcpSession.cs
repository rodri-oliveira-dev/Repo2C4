using Microsoft.Extensions.AI;

namespace Repo2C4.Agent.Tests;

internal sealed class TestMcpSessionFactory(
    IReadOnlyList<AITool>? tools = null,
    string? diagnostic = null) : IAgentMcpSessionFactory
{
    public TestMcpSession Session
    {
        get;
    } = new(tools ?? []);

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

internal sealed class TestMcpSession(IReadOnlyList<AITool> tools) : IAgentMcpSession
{
    public IReadOnlyList<AITool> Tools
    {
        get;
    } = tools;

    public AgentMcpInvocationState InvocationState
    {
        get;
    } = new();

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
