using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Repo2C4.Agent.Tests;

public sealed class AgentMcpTests
{
    [Fact]
    public void CapabilityValidationRejectsMissingAndIncompatibleTools()
    {
        AIFunction inspect = AIFunctionFactory.Create(
            (string repositoryPath) => repositoryPath,
            "inspect_repository");
        AIFunction evidence = AIFunctionFactory.Create(
            (string snapshotId) => snapshotId,
            "get_evidence");

        bool valid = Repo2C4McpCapabilities.TryValidate(
            [inspect, evidence],
            out string? diagnostic);

        Assert.False(valid);
        Assert.Contains("required tools are unavailable", diagnostic, StringComparison.Ordinal);
        Assert.Contains("generate_likec4", diagnostic, StringComparison.Ordinal);

        AIFunction incompatibleGenerate = AIFunctionFactory.Create(
            (string snapshotId, object model) => snapshotId + model,
            "generate_likec4");
        AIFunction snapshot = AIFunctionFactory.Create(
            (string snapshotId) => snapshotId,
            "get_snapshot");
        AIFunction report = AIFunctionFactory.Create(
            (string snapshotId, object model) => snapshotId + model,
            "get_evidence_report");
        AIFunction validate = AIFunctionFactory.Create(
            (string snapshotId, object model) => snapshotId + model,
            "validate_likec4");

        valid = Repo2C4McpCapabilities.TryValidate(
            [inspect, evidence, snapshot, report, incompatibleGenerate, validate],
            out diagnostic);

        Assert.False(valid);
        Assert.Contains("incompatible input schemas", diagnostic, StringComparison.Ordinal);
        Assert.Contains("generate_likec4", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealMcpSessionListsToolsInvokesThroughAgentFrameworkAndForcesPreviewOnly()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string repositoryRoot = FindRepositoryRoot();
        string mcpServer = Path.Combine(
            repositoryRoot,
            "src",
            "Repo2C4.Mcp",
            "bin",
            "Release",
            "net10.0",
            "Repo2C4.Mcp.dll");
        Assert.True(File.Exists(mcpServer), "Build the solution before running Agent MCP integration tests.");

        using TempFixture fixture = TempFixture.Create(
            Path.Combine(repositoryRoot, "examples", "fixtures", "library-only"));

        AgentHostOptions options = new("ollama", "fake-model", "Inspect the authorized repository.")
        {
            RepositoryRoot = fixture.Path,
            McpServerPath = mcpServer,
        };

        Repo2C4McpSessionFactory sessionFactory = new();
        AgentMcpSessionCreation creation = await sessionFactory.CreateAsync(
            options,
            cancellationToken);

        Assert.NotNull(creation.Session);
        Assert.Null(creation.Diagnostic);

        IAgentMcpSession session = creation.Session;
        try
        {
            string[] toolNames =
            [
                .. session.Tools.Select(tool => tool.Name).Order(StringComparer.Ordinal),
            ];
            Assert.Equal(
                [
                    "generate_likec4",
                    "get_evidence",
                    "get_evidence_report",
                    "get_snapshot",
                    "inspect_repository",
                    "validate_likec4",
                ],
                toolNames);

            McpClientTool inspect = Assert.IsType<McpClientTool>(
                session.Tools.Single(tool => tool.Name == "inspect_repository"));
            CallToolResult inspectResult = await inspect.CallAsync(
                new Dictionary<string, object?> { ["repositoryPath"] = "." },
                cancellationToken: cancellationToken);
            Assert.False(inspectResult.IsError is true);
            JsonElement inspectContent = inspectResult.StructuredContent!.Value;
            string snapshotId = inspectContent.GetProperty("snapshotId").GetString()!;

            using JsonDocument modelDocument = JsonDocument.Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(
                        repositoryRoot,
                        "examples",
                        "end-to-end",
                        "architecture.c1.v1.json"),
                    cancellationToken));
            JsonElement model = modelDocument.RootElement.Clone();

            AIFunction preview = Assert.IsAssignableFrom<AIFunction>(
                session.Tools.Single(tool => tool.Name == "generate_likec4"));
            object? previewObject = await preview.InvokeAsync(
                new AIFunctionArguments
                {
                    ["snapshotId"] = snapshotId,
                    ["model"] = model,
                    ["dryRun"] = false,
                    ["write"] = true,
                    ["destinationPath"] = "must-not-exist",
                },
                cancellationToken);
            JsonElement previewEnvelope = Assert.IsType<JsonElement>(previewObject);
            JsonElement previewContent =
                previewEnvelope.TryGetProperty("structuredContent", out JsonElement structuredContent)
                    ? structuredContent
                    : previewEnvelope;
            Assert.True(previewContent.GetProperty("dryRun").GetBoolean());
            Assert.False(previewContent.GetProperty("written").GetBoolean());
            Assert.False(Directory.Exists(Path.Combine(fixture.Path, "must-not-exist")));

            using FunctionCallingTestChatClient chatClient = new(snapshotId, model);
            Repo2C4AgentFactory agentFactory = new();
            AgentSessionRunner runner = new();
            AIAgent agent = agentFactory.Create(chatClient, options, session.Tools);

            string response = await runner.RunAsync(
                agent,
                EvidenceFirstAnalysisPrompt.Build(options),
                cancellationToken);

            Assert.Contains("Confirmed facts", response, StringComparison.Ordinal);
            Assert.Contains("Requires review", response, StringComparison.Ordinal);
            Assert.Contains("Diagnostics/blockers", response, StringComparison.Ordinal);
            Assert.Contains("Proposal", response, StringComparison.Ordinal);
            Assert.True(chatClient.SawMcpTool);
            Assert.True(chatClient.SawFunctionResult);
            Assert.True(chatClient.SawPreviewResult);
        }
        finally
        {
            await session.DisposeAsync();
        }

        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Repo2C4.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Repo2C4 repository root.");
    }

    private sealed class FunctionCallingTestChatClient(
        string snapshotId,
        JsonElement model) : IChatClient
    {
        private int calls;

        public bool SawMcpTool
        {
            get;
            private set;
        }

        public bool SawFunctionResult
        {
            get;
            private set;
        }

        public bool SawPreviewResult
        {
            get;
            private set;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls++;

            if (calls == 1)
            {
                SawMcpTool =
                    options?.Tools?.Any(tool => tool.Name == "inspect_repository") is true;

                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "call-inspect",
                            "inspect_repository",
                            new Dictionary<string, object?>
                            {
                                ["repositoryPath"] = ".",
                            }),
                    ])));
            }

            SawFunctionResult = chatMessages
                .SelectMany(message => message.Contents)
                .Any(content => content is FunctionResultContent);

            if (calls == 2)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            "call-preview",
                            "generate_likec4",
                            new Dictionary<string, object?>
                            {
                                ["snapshotId"] = snapshotId,
                                ["model"] = model,
                                ["dryRun"] = true,
                                ["write"] = false,
                                ["destinationPath"] = null,
                                ["c3ContainerId"] = null,
                            }),
                    ])));
            }

            string serializedResults = JsonSerializer.Serialize(
                chatMessages
                    .SelectMany(message => message.Contents)
                    .OfType<FunctionResultContent>()
                    .Select(result => result.Result));
            SawPreviewResult = serializedResults.Contains(
                "\"dryRun\":true",
                StringComparison.OrdinalIgnoreCase);

            return Task.FromResult(
                new ChatResponse(new ChatMessage(
                    ChatRole.Assistant,
                    """
                    Confirmed facts
                    Repository evidence was obtained through MCP.

                    Requires review
                    The library-only focal boundary remains review-required.

                    Diagnostics/blockers
                    No executable container evidence is present.

                    Proposal
                    A safe C1 preview was produced through the real MCP session.
                    """)));
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
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }
    }

    private sealed class TempFixture : IDisposable
    {
        private readonly string parent;

        private TempFixture(string parent, string path)
        {
            this.parent = parent;
            Path = path;
        }

        public string Path
        {
            get;
        }

        public static TempFixture Create(string source)
        {
            string parent = Directory.CreateTempSubdirectory("repo2c4-agent-mcp-").FullName;
            string destination = System.IO.Path.Combine(parent, "library-only");
            Directory.CreateDirectory(destination);

            foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = System.IO.Path.GetRelativePath(source, sourceFile);
                string target = System.IO.Path.Combine(destination, relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                File.Copy(sourceFile, target);
            }

            return new TempFixture(parent, destination);
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
