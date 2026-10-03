using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Repo2C4.Agent.Tests;

internal sealed record ScriptedChatInvocation(
    int CallIndex,
    IReadOnlyList<ChatMessage> Messages,
    ChatOptions? Options)
{
    public IReadOnlyList<JsonElement> StructuredResults
    {
        get
        {
            List<JsonElement> results = [];

            foreach (FunctionResultContent result in Messages
                         .SelectMany(message => message.Contents)
                         .OfType<FunctionResultContent>())
            {
                if (result.Result is not JsonElement element)
                {
                    continue;
                }

                results.Add(
                    element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("structuredContent", out JsonElement structured)
                        ? structured.Clone()
                        : element.Clone());
            }

            return results;
        }
    }

    public JsonElement FindStructuredResult(string propertyName)
    {
        foreach (JsonElement result in StructuredResults)
        {
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty(propertyName, out _))
            {
                return result;
            }
        }

        throw new InvalidOperationException(
            "Script expected a structured tool result containing property '" + propertyName + "'.");
    }

    public bool HasTool(string name) =>
        Options?.Tools?.Any(tool => string.Equals(tool.Name, name, StringComparison.Ordinal)) is true;
}

internal sealed class ScriptedChatClient(
    params Func<ScriptedChatInvocation, ChatMessage>[] turns) : IChatClient
{
    private readonly Func<ScriptedChatInvocation, ChatMessage>[] turns = turns;
    private readonly List<ScriptedChatInvocation> invocations = [];
    private int calls;

    public IReadOnlyList<ScriptedChatInvocation> Invocations => invocations;

    public int RemainingTurns => turns.Length - Volatile.Read(ref calls);

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int callIndex = Interlocked.Increment(ref calls) - 1;
        if (callIndex >= turns.Length)
        {
            throw new InvalidOperationException(
                "The deterministic chat script has no response for call "
                + callIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ".");
        }

        ScriptedChatInvocation invocation = new(
            callIndex,
            chatMessages.ToArray(),
            options);

        lock (invocations)
        {
            invocations.Add(invocation);
        }

        ChatMessage response = turns[callIndex](invocation);
        return Task.FromResult(new ChatResponse(response));
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

    public void AssertExhausted()
    {
        if (RemainingTurns != 0)
        {
            throw new InvalidOperationException(
                "The deterministic chat script completed with "
                + RemainingTurns.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " unused turn(s).");
        }
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> Empty(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}

internal sealed class DeterministicApprovalPrompt(
    HumanApprovalDecision decision,
    Action<AgentWriteApprovalPlan>? beforeDecision = null)
    : IWriteApprovalPrompt
{
    public int Requests
    {
        get;
        private set;
    }

    public ToolApprovalRequestContent? Request
    {
        get;
        private set;
    }

    public AgentWriteApprovalPlan? Plan
    {
        get;
        private set;
    }

    public ValueTask<HumanApprovalDecision> RequestAsync(
        AgentWriteApprovalPlan plan,
        ToolApprovalRequestContent request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests++;
        Plan = plan;
        Request = request;
        beforeDecision?.Invoke(plan);
        return ValueTask.FromResult(decision);
    }
}

internal sealed class DeterministicAgentHarness : IAsyncDisposable
{
    private static readonly string[] MultiC3Selection = ["el_beta", "el_alpha"];

    private readonly TempRepositoryFixture fixture;
    private readonly IAgentMcpSession session;

    private DeterministicAgentHarness(
        TempRepositoryFixture fixture,
        IAgentMcpSession session,
        AgentHostOptions options)
    {
        this.fixture = fixture;
        this.session = session;
        Options = options;
    }

    public AgentHostOptions Options
    {
        get;
    }

    public string RepositoryPath => fixture.Path;

    public IAgentMcpSession Session => session;

    public static async Task<DeterministicAgentHarness> CreateAsync(
        string fixtureName,
        IReadOnlyList<bool>? validationResults = null,
        string? writeDestination = null,
        string? integrationReportPath = null,
        IReadOnlyList<string>? c3ContainerIds = null,
        CancellationToken cancellationToken = default)
    {
        string repositoryRoot = FindRepositoryRoot();
        string source = System.IO.Path.Combine(
            repositoryRoot,
            "examples",
            "fixtures",
            fixtureName);
        TempRepositoryFixture fixture = TempRepositoryFixture.Create(source);

        string mcpServerPath = TestPaths.McpServer(repositoryRoot);

        if (!File.Exists(mcpServerPath))
        {
            fixture.Dispose();
            throw new InvalidOperationException(
                "Build the solution before running deterministic Agent MCP tests.");
        }

        AgentHostOptions options = new(
            AgentHostOptions.OllamaProvider,
            "deterministic-test-model",
            "Document the repository architecture using only MCP evidence.")
        {
            RepositoryRoot = fixture.Path,
            McpServerPath = mcpServerPath,
            WriteDestination = writeDestination,
            IntegrationReportPath = integrationReportPath,
            C3ContainerIds = c3ContainerIds ?? [],
            MaxValidationAttempts = Math.Min(
                Math.Max(validationResults?.Count ?? 1, 1),
                3),
            MaxWorkflowIterations = 3,
            MaxToolCalls = 80,
            MaxEvidencePages = 30,
            MaxResponseCharacters = 100_000,
            MaxContextCharacters = 200_000,
        };

        AgentMcpSessionCreation creation = await new Repo2C4McpSessionFactory()
            .CreateAsync(options, cancellationToken)
            .ConfigureAwait(false);

        if (creation.Session is null)
        {
            fixture.Dispose();
            throw new InvalidOperationException(
                creation.Diagnostic ?? "The real Repo2C4 MCP session could not be created.");
        }

        IAgentMcpSession session = validationResults is null
            ? creation.Session
            : new ValidationOverrideMcpSession(
                creation.Session,
                validationResults);

        return new DeterministicAgentHarness(fixture, session, options);
    }

    public async Task<ArchitectureWorkflowResult> RunWorkflowAsync(
        ScriptedChatClient chatClient,
        CancellationToken cancellationToken)
    {
        Repo2C4AgentFactory agentFactory = new();
        AIAgent agent = agentFactory.Create(
            chatClient,
            Options,
            session.Tools);

        ArchitectureAnalysisWorkflowRunner runner = new(new AgentSessionRunner());
        return await runner
            .RunAsync(agent, Options, session, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<WriteApprovalResult> RunWriteAsync(
        ScriptedChatClient approvalClient,
        IWriteApprovalPrompt approvalPrompt,
        ArchitectureWorkflowResult workflowResult,
        CancellationToken cancellationToken)
    {
        WriteApprovalRunner runner = new(approvalPrompt);
        return await runner
            .RunAsync(
                approvalClient,
                Options,
                session,
                workflowResult,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await session.DisposeAsync().ConfigureAwait(false);

        try
        {
            await session.Completion
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    public static ChatMessage ToolCalls(params FunctionCallContent[] calls) =>
        new(ChatRole.Assistant, calls.Cast<AIContent>().ToArray());

    public static ChatMessage Text(string text) =>
        new(ChatRole.Assistant, text);

    public static FunctionCallContent Call(
        string callId,
        string name,
        IDictionary<string, object?> arguments) =>
        new(callId, name, arguments);

    public static JsonElement BuildSnapshot(ScriptedChatInvocation invocation)
    {
        JsonElement inspection = invocation.FindStructuredResult("repositoryId");
        JsonElement evidence = invocation.FindStructuredResult("items");

        JsonElement files = invocation.StructuredResults.Single(
            result =>
                result.TryGetProperty("section", out JsonElement section)
                && string.Equals(section.GetString(), "files", StringComparison.Ordinal));
        JsonElement diagnostics = invocation.StructuredResults.Single(
            result =>
                result.TryGetProperty("section", out JsonElement section)
                && string.Equals(section.GetString(), "diagnostics", StringComparison.Ordinal));

        return JsonSerializer.SerializeToElement(new
        {
            schemaVersion = inspection.GetProperty("schemaVersion").GetString(),
            repositoryId = inspection.GetProperty("repositoryId").GetString(),
            files = files.GetProperty("files"),
            evidence = evidence.GetProperty("items"),
            diagnostics = diagnostics.GetProperty("diagnostics"),
        });
    }

    public static JsonElement BuildReviewOnlyModel(
        string level,
        JsonElement snapshot,
        string name = "Evidence-backed repository")
    {
        string[] evidenceIds =
        [
            .. snapshot.GetProperty("evidence")
                .EnumerateArray()
                .Select(item => item.GetProperty("id").GetString())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Cast<string>()
                .Take(3),
        ];

        return JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "1.0",
            level,
            snapshot,
            elements = new[]
            {
                new
                {
                    id = "el_repository",
                    kind = "softwareSystem",
                    name,
                    evidenceIds,
                    status = "requiresReview",
                    reviewReason =
                        "Static repository evidence does not establish a runtime or deployment boundary.",
                },
            },
            relations = Array.Empty<object>(),
        });
    }

    public static JsonElement BuildMultiContainerC2(JsonElement snapshot)
    {
        string[] alphaEvidence = EvidenceIdsForPrefix(snapshot, "src/Alpha/");
        string[] betaEvidence = EvidenceIdsForPrefix(snapshot, "src/Beta/");
        string[] systemEvidence =
        [
            .. alphaEvidence
                .Concat(betaEvidence)
                .Take(3),
        ];

        return JsonSerializer.SerializeToElement(new
        {
            schemaVersion = "1.0",
            level = "C2",
            snapshot,
            elements = new object[]
            {
                new
                {
                    id = "el_system",
                    kind = "softwareSystem",
                    name = "Multi C3 fixture",
                    evidenceIds = systemEvidence,
                    status = "requiresReview",
                    reviewReason = "Repository evidence does not fully establish the software-system boundary.",
                },
                new
                {
                    id = "el_alpha",
                    kind = "container",
                    name = "Alpha API",
                    parentId = "el_system",
                    evidenceIds = alphaEvidence,
                    status = "requiresReview",
                    reviewReason = "Static host evidence does not prove the deployment boundary.",
                },
                new
                {
                    id = "el_beta",
                    kind = "container",
                    name = "Beta API",
                    parentId = "el_system",
                    evidenceIds = betaEvidence,
                    status = "requiresReview",
                    reviewReason = "Static host evidence does not prove the deployment boundary.",
                },
            },
            relations = Array.Empty<object>(),
        });
    }

    private static string[] EvidenceIdsForPrefix(
        JsonElement snapshot,
        string prefix) =>
    [
        .. snapshot.GetProperty("evidence")
            .EnumerateArray()
            .Where(item =>
                item.TryGetProperty("relativePath", out JsonElement path)
                && path.ValueKind == JsonValueKind.String
                && path.GetString() is string relativePath
                && relativePath.StartsWith(prefix, StringComparison.Ordinal))
            .Select(item => item.GetProperty("id").GetString())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    public static string SnapshotId(ScriptedChatInvocation invocation) =>
        invocation.FindStructuredResult("snapshotId")
            .GetProperty("snapshotId")
            .GetString()
        ?? throw new InvalidOperationException("The inspection did not return a snapshot ID.");

    public static IReadOnlyList<Func<ScriptedChatInvocation, ChatMessage>> AnalysisTurns(
        string summary,
        bool produceProposal = true,
        string modelName = "Evidence-backed repository")
    {
        List<Func<ScriptedChatInvocation, ChatMessage>> turns =
        [
            invocation =>
            {
                EnsureTool(invocation, "inspect_repository");
                return ToolCalls(
                    Call(
                        "inspect-1",
                        "inspect_repository",
                        new Dictionary<string, object?>
                        {
                            ["repositoryPath"] = ".",
                        }));
            },
            invocation =>
            {
                string snapshotId = SnapshotId(invocation);
                EnsureTool(invocation, "get_evidence");
                EnsureTool(invocation, "get_snapshot");

                return ToolCalls(
                    Call(
                        "evidence-1",
                        "get_evidence",
                        new Dictionary<string, object?>
                        {
                            ["snapshotId"] = snapshotId,
                            ["pageSize"] = 100,
                        }),
                    Call(
                        "files-1",
                        "get_snapshot",
                        new Dictionary<string, object?>
                        {
                            ["snapshotId"] = snapshotId,
                            ["section"] = "files",
                            ["pageSize"] = 100,
                        }),
                    Call(
                        "diagnostics-1",
                        "get_snapshot",
                        new Dictionary<string, object?>
                        {
                            ["snapshotId"] = snapshotId,
                            ["section"] = "diagnostics",
                            ["pageSize"] = 100,
                        }));
            },
        ];

        if (produceProposal)
        {
            turns.Add(invocation =>
            {
                string snapshotId = invocation.FindStructuredResult("repositoryId")
                    .GetProperty("snapshotId")
                    .GetString()
                    ?? throw new InvalidOperationException("Snapshot ID is unavailable.");
                JsonElement snapshot = BuildSnapshot(invocation);
                JsonElement c1 = BuildReviewOnlyModel("C1", snapshot, modelName);
                JsonElement c2 = BuildReviewOnlyModel("C2", snapshot, modelName);

                EnsureTool(invocation, "generate_likec4");
                return ToolCalls(
                    Call(
                        "preview-c1",
                        "generate_likec4",
                        new Dictionary<string, object?>
                        {
                            ["snapshotId"] = snapshotId,
                            ["model"] = c1,
                            ["dryRun"] = true,
                            ["write"] = false,
                            ["destinationPath"] = null,
                            ["c3ContainerId"] = null,
                        }),
                    Call(
                        "preview-c2",
                        "generate_likec4",
                        new Dictionary<string, object?>
                        {
                            ["snapshotId"] = snapshotId,
                            ["model"] = c2,
                            ["dryRun"] = true,
                            ["write"] = false,
                            ["destinationPath"] = null,
                            ["c3ContainerId"] = null,
                        }));
            });
        }

        turns.Add(_ => Text(summary));
        return turns;
    }

    public static IReadOnlyList<Func<ScriptedChatInvocation, ChatMessage>> MultiC3AnalysisTurns(
        string summary) =>
    [
        invocation =>
        {
            EnsureTool(invocation, "inspect_repository");
            return ToolCalls(
                Call(
                    "inspect-1",
                    "inspect_repository",
                    new Dictionary<string, object?>
                    {
                        ["repositoryPath"] = ".",
                    }));
        },
        invocation =>
        {
            string snapshotId = SnapshotId(invocation);
            EnsureTool(invocation, "get_evidence");
            EnsureTool(invocation, "get_snapshot");

            return ToolCalls(
                Call(
                    "evidence-1",
                    "get_evidence",
                    new Dictionary<string, object?>
                    {
                        ["snapshotId"] = snapshotId,
                        ["pageSize"] = 100,
                    }),
                Call(
                    "files-1",
                    "get_snapshot",
                    new Dictionary<string, object?>
                    {
                        ["snapshotId"] = snapshotId,
                        ["section"] = "files",
                        ["pageSize"] = 100,
                    }),
                Call(
                    "diagnostics-1",
                    "get_snapshot",
                    new Dictionary<string, object?>
                    {
                        ["snapshotId"] = snapshotId,
                        ["section"] = "diagnostics",
                        ["pageSize"] = 100,
                    }));
        },
        invocation =>
        {
            string snapshotId = invocation.FindStructuredResult("repositoryId")
                .GetProperty("snapshotId")
                .GetString()
                ?? throw new InvalidOperationException("Snapshot ID is unavailable.");
            JsonElement snapshot = BuildSnapshot(invocation);
            JsonElement c1 = BuildReviewOnlyModel("C1", snapshot, "Multi C3 fixture");
            JsonElement c2 = BuildMultiContainerC2(snapshot);

            EnsureTool(invocation, "generate_likec4");
            return ToolCalls(
                Call(
                    "preview-c1",
                    "generate_likec4",
                    new Dictionary<string, object?>
                    {
                        ["snapshotId"] = snapshotId,
                        ["model"] = c1,
                        ["dryRun"] = true,
                        ["write"] = false,
                        ["destinationPath"] = null,
                        ["c3ContainerId"] = null,
                        ["c3Containers"] = null,
                    }),
                Call(
                    "preview-c2",
                    "generate_likec4",
                    new Dictionary<string, object?>
                    {
                        ["snapshotId"] = snapshotId,
                        ["model"] = c2,
                        ["dryRun"] = true,
                        ["write"] = false,
                        ["destinationPath"] = null,
                        ["c3ContainerId"] = null,
                        ["c3Containers"] = MultiC3Selection,
                    }));
        },
        _ => Text(summary),
    ];

    public static IReadOnlyList<Func<ScriptedChatInvocation, ChatMessage>> ApprovalTurns() =>
    [
        invocation =>
        {
            EnsureTool(invocation, "apply_validated_likec4_preview");
            return ToolCalls(
                Call(
                    "apply-1",
                    "apply_validated_likec4_preview",
                    new Dictionary<string, object?>()));
        },
        _ => Text("Write approval decision processed."),
    ];

    private static void EnsureTool(
        ScriptedChatInvocation invocation,
        string toolName)
    {
        if (!invocation.HasTool(toolName))
        {
            throw new InvalidOperationException(
                "Expected tool '" + toolName + "' is not available to the scripted Agent Framework call.");
        }
    }

    private static string FindRepositoryRoot() => TestPaths.RepositoryRoot();

    private sealed class ValidationOverrideMcpSession(
        IAgentMcpSession inner,
        IReadOnlyList<bool> validationResults) : IAgentMcpSession
    {
        private readonly IAgentMcpSession inner =
            inner ?? throw new ArgumentNullException(nameof(inner));

        public IReadOnlyList<AITool> Tools
        {
            get;
        } = ReplaceValidationTool(
            inner.Tools,
            validationResults);

        public AgentMcpInvocationState InvocationState => inner.InvocationState;

        public IAgentMcpWriteGateway WriteGateway => inner.WriteGateway;

        public Task Completion => inner.Completion;

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private static IReadOnlyList<AITool> ReplaceValidationTool(
            IReadOnlyList<AITool> tools,
            IReadOnlyList<bool> validationResults)
        {
            Queue<bool> outcomes = new(
                validationResults.SelectMany(outcome => new[] { outcome, outcome }));
            AIFunction validate = AIFunctionFactory.Create(
                (
                    string snapshotId,
                    JsonElement model,
                    string? destinationPath,
                    string? c3ContainerId,
                    string[]? c3Containers) =>
                {
                    _ = snapshotId;
                    _ = model;
                    _ = destinationPath;
                    _ = c3ContainerId;
                    _ = c3Containers;

                    bool isValid = outcomes.Count == 0 || outcomes.Dequeue();
                    return JsonSerializer.SerializeToElement(new
                    {
                        structuredContent = new
                        {
                            isValid,
                            exitCode = isValid ? 0 : 4,
                            diagnostics = isValid
                                ? Array.Empty<object>()
                                : new[]
                                {
                                    new
                                    {
                                        code = "scripted.invalid_likec4",
                                        message = "Deterministic harness requested one invalid LikeC4 validation.",
                                    },
                                },
                        },
                    });
                },
                "validate_likec4",
                "Deterministic validation replacement used only by the Agent end-to-end test harness.");

            return
            [
                .. tools.Select(tool =>
                    string.Equals(tool.Name, "validate_likec4", StringComparison.Ordinal)
                        ? validate
                        : tool),
            ];
        }
    }

    private sealed class TempRepositoryFixture : IDisposable
    {
        private readonly string parent;

        private TempRepositoryFixture(string parent, string path)
        {
            this.parent = parent;
            Path = path;
        }

        public string Path
        {
            get;
        }

        public static TempRepositoryFixture Create(string source)
        {
            if (!Directory.Exists(source))
            {
                throw new DirectoryNotFoundException(
                    "Agent fixture directory was not found: " + source);
            }

            string parent = Directory.CreateTempSubdirectory("repo2c4-agent-e2e-").FullName;
            string destination = System.IO.Path.Combine(parent, "repository");
            Directory.CreateDirectory(destination);

            foreach (string sourceFile in Directory.EnumerateFiles(
                         source,
                         "*",
                         SearchOption.AllDirectories))
            {
                string relative = System.IO.Path.GetRelativePath(source, sourceFile);
                string target = System.IO.Path.Combine(destination, relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.Copy(sourceFile, target);
            }

            return new TempRepositoryFixture(parent, destination);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(parent, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
