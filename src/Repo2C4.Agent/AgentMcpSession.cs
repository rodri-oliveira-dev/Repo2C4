using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Repo2C4.Agent;

/// <summary>Creates one local Repo2C4 MCP stdio session for the Agent host.</summary>
public interface IAgentMcpSessionFactory
{
    ValueTask<AgentMcpSessionCreation> CreateAsync(
        AgentHostOptions options,
        CancellationToken cancellationToken);
}

/// <summary>Result of initializing the local Repo2C4 MCP capability boundary.</summary>
public sealed record AgentMcpSessionCreation(IAgentMcpSession? Session, string? Diagnostic)
{
    public static AgentMcpSessionCreation Success(IAgentMcpSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new AgentMcpSessionCreation(session, null);
    }

    public static AgentMcpSessionCreation Failure(string diagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnostic);
        return new AgentMcpSessionCreation(null, diagnostic);
    }
}

/// <summary>One initialized Repo2C4 MCP session and its safe Agent Framework tools.</summary>
public interface IAgentMcpSession : IAsyncDisposable
{
    IReadOnlyList<AITool> Tools
    {
        get;
    }

    AgentMcpInvocationState InvocationState
    {
        get;
    }

    IAgentMcpWriteGateway WriteGateway
    {
        get;
    }

    Task Completion
    {
        get;
    }
}

/// <summary>One MCP-accepted architecture proposal captured without referencing Repo2C4.Core.</summary>
public sealed record AgentArchitectureProposal(
    string Level,
    string SnapshotId,
    JsonElement Model,
    string? C3ContainerId)
{
    public IReadOnlyList<string> C3ContainerIds
    {
        get;
        init;
    } = string.IsNullOrWhiteSpace(C3ContainerId) ? [] : [C3ContainerId];
}

/// <summary>Run-local capture of successful preview calls made through the safe MCP boundary.</summary>
public sealed class AgentMcpInvocationState
{
    private readonly object gate = new();

    public AgentMcpInvocationState(AgentExecutionContext? execution = null)
    {
        Execution = execution ?? new AgentExecutionContext(
            new AgentExecutionBudgets(
                TimeSpan.FromSeconds(90),
                40,
                3,
                20,
                32_000,
                64_000));
    }

    public AgentExecutionContext Execution
    {
        get;
    }
    private readonly Dictionary<string, AgentArchitectureProposal> proposals =
        new(StringComparer.Ordinal);

    public void BeginAttempt()
    {
        lock (gate)
        {
            proposals.Clear();
        }
    }

    public void RecordPreview(
        string snapshotId,
        JsonElement model,
        string? c3ContainerId,
        IReadOnlyList<string>? c3ContainerIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        string[] selectedC3Containers =
        [
            .. (c3ContainerIds ?? [])
                .Concat(string.IsNullOrWhiteSpace(c3ContainerId) ? [] : [c3ContainerId])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal),
        ];

        string level =
            model.ValueKind == JsonValueKind.Object
            && model.TryGetProperty("level", out JsonElement levelElement)
            && levelElement.ValueKind == JsonValueKind.String
                ? levelElement.GetString() ?? "unknown"
                : "unknown";

        AgentArchitectureProposal proposal = new(
            level,
            snapshotId,
            model.Clone(),
            selectedC3Containers.Length == 1 ? selectedC3Containers[0] : null)
        {
            C3ContainerIds = selectedC3Containers,
        };

        lock (gate)
        {
            proposals[level] = proposal;
        }
    }

    public IReadOnlyList<AgentArchitectureProposal> SnapshotProposals()
    {
        lock (gate)
        {
            return
            [
                .. proposals.Values
                    .OrderBy(
                        proposal => proposal.Level switch
                        {
                            "C1" => 0,
                            "C2" => 1,
                            _ => 2,
                        })
                    .ThenBy(proposal => proposal.Level, StringComparer.Ordinal),
            ];
        }
    }
}

/// <summary>Versioned capability requirements for the Repo2C4 MCP used by the agent.</summary>
public static class Repo2C4McpCapabilities
{
    private static readonly string[] RequiredNames =
    [
        "inspect_repository",
        "get_evidence",
        "get_snapshot",
        "get_evidence_report",
        "generate_likec4",
        "validate_likec4",
    ];

    private static readonly IReadOnlyDictionary<string, string[]> RequiredSchemas =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["inspect_repository"] = ["repositoryPath"],
            ["get_evidence"] = ["snapshotId"],
            ["get_snapshot"] = ["snapshotId"],
            ["get_evidence_report"] = ["snapshotId", "model"],
            ["generate_likec4"] = ["snapshotId", "model", "dryRun", "write"],
            ["validate_likec4"] = ["snapshotId", "model"],
        };

    public static IReadOnlyCollection<string> RequiredToolNames => RequiredNames;

    public static bool TryValidate(
        IEnumerable<AITool> tools,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(tools);

        Dictionary<string, AITool> available = tools
            .GroupBy(tool => tool.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        string[] missing =
        [
            .. RequiredSchemas.Keys
                .Where(name => !available.ContainsKey(name))
                .Order(StringComparer.Ordinal),
        ];
        if (missing.Length > 0)
        {
            diagnostic =
                "Repo2C4 Agent MCP capability error: required tools are unavailable: "
                + string.Join(", ", missing)
                + ".";
            return false;
        }

        string[] incompatible =
        [
            .. RequiredSchemas
                .Where(requirement =>
                    !HasRequiredSchema(
                        available[requirement.Key],
                        requirement.Value))
                .Select(requirement => requirement.Key)
                .Order(StringComparer.Ordinal),
        ];
        if (incompatible.Length > 0)
        {
            diagnostic =
                "Repo2C4 Agent MCP capability error: required tools have incompatible input schemas: "
                + string.Join(", ", incompatible)
                + ".";
            return false;
        }

        diagnostic = null;
        return true;
    }

    private static bool HasRequiredSchema(AITool tool, IReadOnlyList<string> requiredProperties)
    {
        if (tool is not AIFunction function
            || function.JsonSchema.ValueKind != JsonValueKind.Object
            || !function.JsonSchema.TryGetProperty("properties", out JsonElement properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return requiredProperties.All(name => properties.TryGetProperty(name, out _));
    }
}

/// <summary>Official MCP SDK stdio connector for the locally distributed Repo2C4 MCP server.</summary>
public sealed class Repo2C4McpSessionFactory : IAgentMcpSessionFactory
{
    private const string InstalledMcpCommand = "repo2c4-mcp";

    public async ValueTask<AgentMcpSessionCreation> CreateAsync(
        AgentHostOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryValidateRepositoryRoot(options.RepositoryRoot, out string? repositoryRoot))
        {
            return AgentMcpSessionCreation.Failure(
                "Repo2C4 Agent MCP configuration error: provide --repository-root as an existing absolute local directory.");
        }

        if (!TryResolveServerCommand(
                options.McpServerPath,
                repositoryRoot!,
                out string command,
                out string[] arguments))
        {
            return AgentMcpSessionCreation.Failure(
                "Repo2C4 Agent MCP configuration error: --mcp-server-path must identify an existing absolute local executable or Repo2C4.Mcp.dll.");
        }

        StdioClientTransport transport = new(new StdioClientTransportOptions
        {
            Name = "Repo2C4",
            Command = command,
            Arguments = arguments,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = CreateMinimalEnvironment(),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        });

        McpClient? client = null;
        try
        {
            client = await McpClient
                .CreateAsync(transport, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            IList<McpClientTool> discovered = await client
                .ListToolsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!Repo2C4McpCapabilities.TryValidate(discovered, out string? capabilityError))
            {
                await DisposeClientAsync(client).ConfigureAwait(false);
                client = null;
                return AgentMcpSessionCreation.Failure(capabilityError!);
            }

            AgentExecutionContext execution =
                options.ExecutionContext
                ?? new AgentExecutionContext(AgentExecutionBudgets.FromOptions(options));
            AgentMcpInvocationState invocationState = new(execution);
            IReadOnlyList<AITool> safeTools = AgentMcpToolPolicy.CreateSafeTools(
                discovered,
                options.C3ContainerId,
                invocationState,
                options.IntegrationReportPath,
                options.C3ContainerIds);
            McpClientTool generateLikeC4 = discovered.Single(
                tool => string.Equals(
                    tool.Name,
                    "generate_likec4",
                    StringComparison.Ordinal));
            IAgentMcpWriteGateway writeGateway =
                new Repo2C4McpWriteGateway(generateLikeC4, invocationState.Execution);
#pragma warning disable CA2000 // Ownership transfers to the returned session and is disposed by the host.
            IAgentMcpSession session = new Repo2C4McpSession(
                client,
                safeTools,
                invocationState,
                writeGateway);
#pragma warning restore CA2000
            client = null;
            return AgentMcpSessionCreation.Success(session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await DisposeClientAsync(client).ConfigureAwait(false);
            throw;
        }
#pragma warning disable CA1031 // MCP startup is a process boundary; raw SDK/process errors may expose local paths.
        catch (Exception)
#pragma warning restore CA1031
        {
            await DisposeClientAsync(client).ConfigureAwait(false);
            return AgentMcpSessionCreation.Failure(
                "Repo2C4 Agent MCP connection error: the local Repo2C4 MCP server could not be started or initialized.");
        }
    }

    private static async ValueTask DisposeClientAsync(McpClient? client)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Startup cleanup must not replace the controlled MCP diagnostic/cancellation.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static bool TryValidateRepositoryRoot(
        string? value,
        out string? repositoryRoot)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Path.IsPathFullyQualified(value)
            && Directory.Exists(value))
        {
            repositoryRoot = Path.GetFullPath(value);
            return true;
        }

        repositoryRoot = null;
        return false;
    }

    private static bool TryResolveServerCommand(
        string? serverPath,
        string repositoryRoot,
        out string command,
        out string[] arguments)
    {
        if (string.IsNullOrWhiteSpace(serverPath))
        {
            command = InstalledMcpCommand;
            arguments = ["--repository-root", repositoryRoot];
            return true;
        }

        if (!Path.IsPathFullyQualified(serverPath) || !File.Exists(serverPath))
        {
            command = string.Empty;
            arguments = [];
            return false;
        }

        string fullPath = Path.GetFullPath(serverPath);
        if (string.Equals(Path.GetExtension(fullPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            command = "dotnet";
            arguments = [fullPath, "--repository-root", repositoryRoot];
            return true;
        }

        command = fullPath;
        arguments = ["--repository-root", repositoryRoot];
        return true;
    }

    private static IDictionary<string, string?> CreateMinimalEnvironment()
    {
        Dictionary<string, string?> environment = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in new[]
        {
            "PATH",
            "DOTNET_ROOT",
            "DOTNET_CLI_HOME",
            "HOME",
            "USERPROFILE",
            "TEMP",
            "TMP",
            "TMPDIR",
            "LANG",
        })
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    private sealed class Repo2C4McpSession(
        McpClient client,
        IReadOnlyList<AITool> tools,
        AgentMcpInvocationState invocationState,
        IAgentMcpWriteGateway writeGateway) : IAgentMcpSession
    {
        private readonly McpClient client =
            client ?? throw new ArgumentNullException(nameof(client));

        public IReadOnlyList<AITool> Tools
        {
            get;
        } = tools ?? throw new ArgumentNullException(nameof(tools));

        public AgentMcpInvocationState InvocationState
        {
            get;
        } = invocationState ?? throw new ArgumentNullException(nameof(invocationState));

        public IAgentMcpWriteGateway WriteGateway
        {
            get;
        } = writeGateway ?? throw new ArgumentNullException(nameof(writeGateway));

        public Task Completion
        {
            get;
        } = ObserveCompletionAsync(client);

        public ValueTask DisposeAsync() => client.DisposeAsync();

        private static async Task ObserveCompletionAsync(McpClient client)
        {
            _ = await client.Completion.ConfigureAwait(false);
        }
    }
}

/// <summary>Applies host-enforced safety policy to MCP tools before exposing them to the model.</summary>
public static class AgentMcpToolPolicy
{
    public static IReadOnlyList<AITool> CreateSafeTools(
        IEnumerable<AITool> discovered,
        string? authorizedC3ContainerId,
        AgentMcpInvocationState? invocationState = null,
        string? authorizedIntegrationReportPath = null,
        IReadOnlyList<string>? authorizedC3ContainerIds = null)
    {
        ArgumentNullException.ThrowIfNull(discovered);

        string[] authorizedC3Containers =
        [
            .. (authorizedC3ContainerIds ?? [])
                .Concat(string.IsNullOrWhiteSpace(authorizedC3ContainerId) ? [] : [authorizedC3ContainerId])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal),
        ];

        List<AITool> tools = [];
        foreach (AITool tool in discovered)
        {
            if (!Repo2C4McpCapabilities.RequiredToolNames.Contains(tool.Name, StringComparer.Ordinal))
            {
                continue;
            }

            if (tool is not AIFunction function)
            {
                continue;
            }

            AIFunction safeFunction =
                tool.Name switch
                {
                    "inspect_repository" => new AuthorizedRootInspectionMcpFunction(
                        function,
                        authorizedIntegrationReportPath),
                    "generate_likec4" => new PreviewOnlyMcpFunction(
                        function,
                        authorizedC3Containers,
                        invocationState),
                    "validate_likec4" => new ProposalOnlyValidationMcpFunction(
                        function,
                        authorizedC3Containers),
                    _ => function,
                };

            tools.Add(
                invocationState is null
                    ? safeFunction
                    : new GovernedMcpFunction(
                        safeFunction,
                        invocationState.Execution));
        }

        return tools;
    }

    private sealed class AuthorizedRootInspectionMcpFunction(
        AIFunction inner,
        string? authorizedIntegrationReportPath)
        : DelegatingAIFunction(inner)
    {
        public override string Description =>
            "Inspect the complete host-authorized repository root. This Agent wrapper forces repositoryPath='.'.";

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(arguments);

            AIFunctionArguments safeArguments = [];
            foreach ((string key, object? value) in arguments)
            {
                if (key is not "repositoryPath" and not "integrationReportPath")
                {
                    safeArguments[key] = value;
                }
            }

            safeArguments["repositoryPath"] = ".";
            if (authorizedIntegrationReportPath is not null)
            {
                safeArguments["integrationReportPath"] = authorizedIntegrationReportPath;
            }

            return base.InvokeCoreAsync(safeArguments, cancellationToken);
        }
    }

    private sealed class PreviewOnlyMcpFunction(
        AIFunction inner,
        string[] authorizedC3Containers,
        AgentMcpInvocationState? invocationState)
        : DelegatingAIFunction(inner)
    {
        public override string Description =>
            authorizedC3Containers.Length == 0
                ? "Preview deterministic LikeC4. This Agent wrapper forces dryRun=true, write=false, ignores destinationPath and disables C3."
                : "Preview deterministic LikeC4. This Agent wrapper forces dryRun=true, write=false, ignores destinationPath and permits only an explicitly requested subset of the host-authorized C3 containers.";

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(arguments);

            AIFunctionArguments safeArguments = [];
            foreach ((string key, object? value) in arguments)
            {
                if (key is "dryRun" or "write" or "destinationPath" or "c3ContainerId" or "c3Containers")
                {
                    continue;
                }

                safeArguments[key] = value;
            }

            C3RequestSelection selection = SelectAuthorizedC3(
                arguments,
                authorizedC3Containers);

            safeArguments["dryRun"] = true;
            safeArguments["write"] = false;
            safeArguments["destinationPath"] = null;
            safeArguments["c3ContainerId"] = selection.LegacyContainerId;
            safeArguments["c3Containers"] =
                selection.ContainerIds.Length == 0 ? null : selection.ContainerIds;

            object? result = await base
                .InvokeCoreAsync(safeArguments, cancellationToken)
                .ConfigureAwait(false);

            if (invocationState is not null
                && !IsMcpErrorResult(result)
                && safeArguments.TryGetValue("snapshotId", out object? snapshotValue)
                && snapshotValue is string snapshotId
                && safeArguments.TryGetValue("model", out object? modelValue)
                && TryGetJsonElement(modelValue, out JsonElement model)
                && model.ValueKind == JsonValueKind.Object)
            {
                invocationState.RecordPreview(
                    snapshotId,
                    model,
                    selection.LegacyContainerId,
                    selection.ContainerIds);
            }

            return result;
        }
    }

    private sealed class ProposalOnlyValidationMcpFunction(
        AIFunction inner,
        string[] authorizedC3Containers)
        : DelegatingAIFunction(inner)
    {
        public override string Description =>
            "Validate only an in-memory proposal. This Agent wrapper ignores destinationPath and permits only an explicitly requested subset of the host-authorized C3 containers.";

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(arguments);

            AIFunctionArguments safeArguments = [];
            foreach ((string key, object? value) in arguments)
            {
                if (key is "destinationPath" or "c3ContainerId" or "c3Containers")
                {
                    continue;
                }

                safeArguments[key] = value;
            }

            C3RequestSelection selection = SelectAuthorizedC3(
                arguments,
                authorizedC3Containers);
            safeArguments["destinationPath"] = null;
            safeArguments["c3ContainerId"] = selection.LegacyContainerId;
            safeArguments["c3Containers"] =
                selection.ContainerIds.Length == 0 ? null : selection.ContainerIds;

            return base.InvokeCoreAsync(safeArguments, cancellationToken);
        }
    }

    private static C3RequestSelection SelectAuthorizedC3(
        AIFunctionArguments arguments,
        IReadOnlyList<string> authorized)
    {
        string[] requested = ReadRequestedC3(arguments);
        HashSet<string> allowed = authorized.ToHashSet(StringComparer.Ordinal);
        string[] selected =
        [
            .. requested
                .Where(allowed.Contains)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal),
        ];

        bool legacyOnly =
            arguments.TryGetValue("c3ContainerId", out object? legacyValue)
            && legacyValue is not null
            && !arguments.TryGetValue("c3Containers", out object? multiValue);

        if (legacyOnly && authorized.Count == 1 && selected.Length == 0)
        {
            selected = [authorized[0]];
        }

        return new C3RequestSelection(
            legacyOnly && selected.Length == 1 ? selected[0] : null,
            selected);
    }

    private static string[] ReadRequestedC3(AIFunctionArguments arguments)
    {
        List<string> requested = [];

        if (arguments.TryGetValue("c3ContainerId", out object? legacy) &&
            legacy is string legacyId &&
            !string.IsNullOrWhiteSpace(legacyId))
        {
            requested.Add(legacyId);
        }

        if (arguments.TryGetValue("c3Containers", out object? multiple) &&
            multiple is not null)
        {
            switch (multiple)
            {
                case JsonElement element when element.ValueKind == JsonValueKind.Array:
                    requested.AddRange(
                        element.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString())
                            .Where(id => !string.IsNullOrWhiteSpace(id))
                            .Cast<string>());
                    break;

                case IEnumerable<string> strings:
                    requested.AddRange(strings.Where(id => !string.IsNullOrWhiteSpace(id)));
                    break;

                case IEnumerable<object?> objects:
                    requested.AddRange(
                        objects.OfType<string>()
                            .Where(id => !string.IsNullOrWhiteSpace(id)));
                    break;
            }
        }

        return [.. requested];
    }

    private sealed record C3RequestSelection(
        string? LegacyContainerId,
        string[] ContainerIds);

    private static bool IsMcpErrorResult(object? result) =>
        result is JsonElement element
        && element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("isError", out JsonElement isError)
        && isError.ValueKind == JsonValueKind.True;

    private static bool TryGetJsonElement(object? value, out JsonElement element)
    {
        switch (value)
        {
            case JsonElement jsonElement:
                element = jsonElement;
                return true;

            case JsonDocument document:
                element = document.RootElement.Clone();
                return true;

            default:
                element = default;
                return false;
        }
    }
}
