using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace Repo2C4.Agent.Tests;

public sealed class WriteApprovalTests
{
    [Fact]
    public async Task ApproveUsesRealFrameworkApprovalRequestBeforeApplyingExactPlan()
    {
        FakeWriteGateway gateway = new(AgentWriteApplyStatus.Applied);
        TestMcpSession session = CreateSession(gateway);
        using ApprovalCallingChatClient chatClient = new();
        RecordingApprovalPrompt prompt = new(
            HumanApprovalDecision.Approve,
            () => gateway.ApplyCalls);
        WriteApprovalRunner runner = new(prompt);

        WriteApprovalResult result = await runner.RunAsync(
            chatClient,
            CreateOptions(),
            session,
            CreateValidatedWorkflowResult(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WriteApprovalStatus.Applied, result.Status);
        Assert.Equal(1, gateway.ApplyCalls);
        Assert.Equal(1, gateway.Writes);
        Assert.Equal(0, prompt.ApplyCallsAtPrompt);
        Assert.NotNull(prompt.Request);
        Assert.Equal(
            "apply_validated_likec4_preview",
            Assert.IsType<FunctionCallContent>(prompt.Request.ToolCall).Name);
        Assert.Equal(["architecture/c2"], result.Destinations);
    }

    [Fact]
    public async Task ProviderFailureAfterApplyPreservesWrittenDestinations()
    {
        FakeWriteGateway gateway = new(AgentWriteApplyStatus.Applied);
        TestMcpSession session = CreateSession(gateway);
        using ApprovalCallingChatClient chatClient = new(failAfterFunctionResult: true);
        RecordingApprovalPrompt prompt = new(
            HumanApprovalDecision.Approve,
            () => gateway.ApplyCalls);

        WriteApprovalResult result = await new WriteApprovalRunner(prompt).RunAsync(
            chatClient,
            CreateOptions(),
            session,
            CreateValidatedWorkflowResult(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WriteApprovalStatus.Applied, result.Status);
        Assert.Equal(["architecture/c2"], result.Destinations);
        Assert.Equal(1, gateway.ApplyCalls);
        Assert.Equal(1, gateway.Writes);
    }

    [Fact]
    public async Task DenyDoesNotInvokeProtectedWriteTool()
    {
        FakeWriteGateway gateway = new(AgentWriteApplyStatus.Applied);
        TestMcpSession session = CreateSession(gateway);
        using ApprovalCallingChatClient chatClient = new();
        RecordingApprovalPrompt prompt = new(
            HumanApprovalDecision.Deny,
            () => gateway.ApplyCalls);

        WriteApprovalResult result = await new WriteApprovalRunner(prompt).RunAsync(
            chatClient,
            CreateOptions(),
            session,
            CreateValidatedWorkflowResult(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WriteApprovalStatus.Denied, result.Status);
        Assert.Equal(0, gateway.ApplyCalls);
        Assert.Equal(0, gateway.Writes);
        Assert.NotNull(prompt.Request);
    }

    [Fact]
    public async Task CancelDoesNotSendApprovalOrWrite()
    {
        FakeWriteGateway gateway = new(AgentWriteApplyStatus.Applied);
        TestMcpSession session = CreateSession(gateway);
        using ApprovalCallingChatClient chatClient = new();
        RecordingApprovalPrompt prompt = new(
            HumanApprovalDecision.Cancel,
            () => gateway.ApplyCalls);

        WriteApprovalResult result = await new WriteApprovalRunner(prompt).RunAsync(
            chatClient,
            CreateOptions(),
            session,
            CreateValidatedWorkflowResult(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WriteApprovalStatus.Cancelled, result.Status);
        Assert.Equal(0, gateway.ApplyCalls);
        Assert.Equal(0, gateway.Writes);
    }

    [Fact]
    public async Task StalePreviewAfterApprovalRequiresNewCycleAndWritesNothing()
    {
        FakeWriteGateway gateway = new(AgentWriteApplyStatus.StalePreview);
        TestMcpSession session = CreateSession(gateway);
        using ApprovalCallingChatClient chatClient = new();
        RecordingApprovalPrompt prompt = new(
            HumanApprovalDecision.Approve,
            () => gateway.ApplyCalls);

        WriteApprovalResult result = await new WriteApprovalRunner(prompt).RunAsync(
            chatClient,
            CreateOptions(),
            session,
            CreateValidatedWorkflowResult(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WriteApprovalStatus.StalePreview, result.Status);
        Assert.Equal(1, gateway.ApplyCalls);
        Assert.Equal(0, gateway.Writes);
        Assert.Contains("stale", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConflictBlocksApprovalAndWritesNothing()
    {
        FakeWriteGateway gateway = new(
            AgentWriteApplyStatus.Applied,
            previewHasConflicts: true);
        TestMcpSession session = CreateSession(gateway);
        using ApprovalCallingChatClient chatClient = new();
        RecordingApprovalPrompt prompt = new(
            HumanApprovalDecision.Approve,
            () => gateway.ApplyCalls);

        WriteApprovalResult result = await new WriteApprovalRunner(prompt).RunAsync(
            chatClient,
            CreateOptions(),
            session,
            CreateValidatedWorkflowResult(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WriteApprovalStatus.Conflict, result.Status);
        Assert.Equal(0, gateway.ApplyCalls);
        Assert.Equal(0, gateway.Writes);
        Assert.Null(prompt.Request);
    }

    [Fact]
    public async Task ApprovalPromptShowsDestinationChangesFilesAndReviewItems()
    {
        AgentWriteApprovalPlan plan = CreatePlan(hasConflicts: false);
        ToolApprovalRequestContent request = new(
            "approval-1",
            new FunctionCallContent(
                "call-1",
                "apply_validated_likec4_preview",
                new Dictionary<string, object?>()));
        using StringReader input = new("n" + Environment.NewLine);
        using StringWriter output = new();
        ConsoleWriteApprovalPrompt prompt = new(input, output);

        HumanApprovalDecision decision = await prompt.RequestAsync(
            plan,
            request,
            TestContext.Current.CancellationToken);

        string text = output.ToString();
        Assert.Equal(HumanApprovalDecision.Deny, decision);
        Assert.Contains("architecture/c2", text, StringComparison.Ordinal);
        Assert.Contains("create model.c4", text, StringComparison.Ordinal);
        Assert.Contains("file: model.c4", text, StringComparison.Ordinal);
        Assert.Contains("requiresReview: el_candidate", text, StringComparison.Ordinal);
        Assert.Contains("approval-1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteDestinationMustBeExplicitSafeAndGoalBound()
    {
        bool parsed = AgentHostOptions.TryParse(
            [
                "--provider", "ollama",
                "--model", "model",
                "--goal", "Document architecture",
                "--write-destination", "docs/architecture",
            ],
            out AgentHostOptions? options,
            out string? error);

        Assert.True(parsed, error);
        Assert.NotNull(options);
        Assert.Equal("docs/architecture", options.WriteDestination);

        parsed = AgentHostOptions.TryParse(
            [
                "--provider", "ollama",
                "--model", "model",
                "--goal", "Document architecture",
                "--write-destination", "../outside",
            ],
            out _,
            out error);

        Assert.False(parsed);
        Assert.Contains("--write-destination", error, StringComparison.Ordinal);

        parsed = AgentHostOptions.TryParse(
            [
                "--provider", "ollama",
                "--model", "model",
                "--write-destination", "architecture",
            ],
            out _,
            out error);

        Assert.False(parsed);
        Assert.Contains("requires --goal", error, StringComparison.Ordinal);
    }

    private static AgentHostOptions CreateOptions() =>
        new("ollama", "fake-model", "Document architecture")
        {
            WriteDestination = "architecture",
        };

    private static ArchitectureWorkflowResult CreateValidatedWorkflowResult() =>
        new(
            ArchitectureWorkflowStatus.RequiresReview,
            1,
            "Validated review-only proposal.",
            []);

    private static TestMcpSession CreateSession(FakeWriteGateway gateway)
    {
        TestMcpSession session = new([], gateway);
        session.InvocationState.RecordPreview(
            "snap-1",
            CreateModel(),
            c3ContainerId: null);
        return session;
    }

    private static AgentWriteApprovalPlan CreatePlan(bool hasConflicts)
    {
        AgentWritePlanItem item = new(
            "C2",
            "snap-1",
            CreateModel(),
            "architecture/c2",
            null,
            [new AgentWriteFilePreview("model.c4", 128)],
            [new AgentWriteChangePreview("model.c4", "create", null, "ABC123")],
            ["el_candidate"],
            "FINGERPRINT",
            hasConflicts);

        return new AgentWriteApprovalPlan("architecture", [item]);
    }

    private static JsonElement CreateModel()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "schemaVersion": "1.0",
              "level": "C2",
              "snapshot": {
                "schemaVersion": "1.0",
                "repositoryId": "repo_test",
                "files": [],
                "evidence": [],
                "diagnostics": []
              },
              "elements": [
                {
                  "id": "el_candidate",
                  "kind": "softwareSystem",
                  "name": "Candidate",
                  "evidenceIds": [],
                  "status": "requiresReview",
                  "reviewReason": "Human review is required."
                }
              ],
              "relations": []
            }
            """);

        return document.RootElement.Clone();
    }

    private sealed class FakeWriteGateway(
        AgentWriteApplyStatus applyStatus,
        bool previewHasConflicts = false) : IAgentMcpWriteGateway
    {
        public int ApplyCalls
        {
            get;
            private set;
        }

        public int Writes
        {
            get;
            private set;
        }

        public ValueTask<AgentWriteApprovalPlan> PrepareAsync(
            IReadOnlyList<AgentArchitectureProposal> proposals,
            string destinationRoot,
            CancellationToken cancellationToken)
        {
            _ = proposals;
            cancellationToken.ThrowIfCancellationRequested();

            AgentWriteApprovalPlan plan = CreatePlan(previewHasConflicts) with
            {
                DestinationRoot = destinationRoot,
            };
            return ValueTask.FromResult(plan);
        }

        public ValueTask<AgentWriteApplyResult> ApplyAsync(
            AgentWriteApprovalPlan plan,
            CancellationToken cancellationToken)
        {
            _ = plan;
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCalls++;

            if (applyStatus == AgentWriteApplyStatus.Applied)
            {
                Writes++;
                return ValueTask.FromResult(
                    new AgentWriteApplyResult(
                        AgentWriteApplyStatus.Applied,
                        ["architecture/c2"],
                        null));
            }

            return ValueTask.FromResult(
                new AgentWriteApplyResult(
                    applyStatus,
                    [],
                    applyStatus == AgentWriteApplyStatus.StalePreview
                        ? "The approved preview is stale; a new preview and approval are required."
                        : "Managed output conflict prevented the approved write."));
        }
    }

    private sealed class RecordingApprovalPrompt(
        HumanApprovalDecision decision,
        Func<int> applyCalls) : IWriteApprovalPrompt
    {
        public ToolApprovalRequestContent? Request
        {
            get;
            private set;
        }

        public int ApplyCallsAtPrompt
        {
            get;
            private set;
        }

        public ValueTask<HumanApprovalDecision> RequestAsync(
            AgentWriteApprovalPlan plan,
            ToolApprovalRequestContent request,
            CancellationToken cancellationToken)
        {
            _ = plan;
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            ApplyCallsAtPrompt = applyCalls();
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class ApprovalCallingChatClient(bool failAfterFunctionResult = false) : IChatClient
    {
        private readonly bool failAfterFunctionResult = failAfterFunctionResult;
        private int calls;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls++;

            if (calls == 1)
            {
                Assert.Contains(
                    options?.Tools ?? [],
                    tool => tool.Name == "apply_validated_likec4_preview");

                return Task.FromResult(
                    new ChatResponse(
                        new ChatMessage(
                            ChatRole.Assistant,
                            [
                                new FunctionCallContent(
                                    "write-call-1",
                                    "apply_validated_likec4_preview",
                                    new Dictionary<string, object?>()),
                            ])));
            }

            bool hasFunctionResult = chatMessages
                .SelectMany(message => message.Contents)
                .Any(content => content is FunctionResultContent);

            if (hasFunctionResult && failAfterFunctionResult)
            {
                throw new InvalidOperationException(
                    "Deterministic provider failure after protected apply.");
            }

            return Task.FromResult(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        hasFunctionResult ? "write processed" : "approval processed")));
        }

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
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }
}
