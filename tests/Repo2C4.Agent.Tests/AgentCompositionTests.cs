using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace Repo2C4.Agent.Tests;

public sealed class AgentCompositionTests
{
    [Fact]
    public async Task FrameworkAgentUsesInjectedChatClientVersionedInstructionsModelAndTools()
    {
        AgentHostOptions options = new("fake", "unit-model", "Summarize the supplied evidence.");
        using TestChatClient chatClient = new("session-complete");
        AIFunction markerTool = AIFunctionFactory.Create(() => "ok", "marker_tool");
        Repo2C4AgentFactory factory = new();
        AgentSessionRunner runner = new();

        AIAgent agent = factory.Create(chatClient, options, [markerTool]);
        string response = await runner.RunAsync(
            agent,
            options.Prompt!,
            TestContext.Current.CancellationToken);

        Assert.IsType<ChatClientAgent>(agent);
        Assert.Equal("session-complete", response);
        Assert.NotNull(chatClient.LastOptions);
        Assert.Equal("unit-model", chatClient.LastOptions.ModelId);
        Assert.Contains(
            Repo2C4AgentInstructions.Version,
            chatClient.LastOptions.Instructions,
            StringComparison.Ordinal);
        Assert.Contains(chatClient.LastOptions.Tools, tool => tool.Name == "marker_tool");
        Assert.NotNull(chatClient.LastMessages);
        Assert.Contains(
            chatClient.LastMessages,
            message => message.Role == ChatRole.User &&
                string.Equals(message.Text, options.Prompt, StringComparison.Ordinal));
    }

    [Fact]
    public void AgentAssemblyDoesNotReferenceCoreCliOrMcpProjects()
    {
        string?[] references = typeof(Repo2C4AgentFactory)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.DoesNotContain("Repo2C4.Core", references);
        Assert.DoesNotContain("Repo2C4.Cli", references);
        Assert.DoesNotContain("Repo2C4.Mcp", references);
    }
}
