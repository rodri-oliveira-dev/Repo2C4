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

    Task Completion
    {
        get;
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

        try
        {
            McpClient client = await McpClient
                .CreateAsync(transport, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            IList<McpClientTool> discovered = await client
                .ListToolsAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!Repo2C4McpCapabilities.TryValidate(discovered, out string? capabilityError))
            {
                await client.DisposeAsync().ConfigureAwait(false);
                return AgentMcpSessionCreation.Failure(capabilityError!);
            }

            IReadOnlyList<AITool> safeTools = AgentMcpToolPolicy.CreateSafeTools(
                discovered,
                options.C3ContainerId);
#pragma warning disable CA2000 // Ownership transfers to AgentMcpSessionCreation and is disposed by the host.
            IAgentMcpSession session = new Repo2C4McpSession(client, safeTools);
#pragma warning restore CA2000
            return AgentMcpSessionCreation.Success(session);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // MCP startup is a process boundary; raw SDK/process errors may expose local paths.
        catch (Exception)
#pragma warning restore CA1031
        {
            return AgentMcpSessionCreation.Failure(
                "Repo2C4 Agent MCP connection error: the local Repo2C4 MCP server could not be started or initialized.");
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
        IReadOnlyList<AITool> tools) : IAgentMcpSession
    {
        private readonly McpClient client =
            client ?? throw new ArgumentNullException(nameof(client));

        public IReadOnlyList<AITool> Tools
        {
            get;
        } = tools ?? throw new ArgumentNullException(nameof(tools));

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
        string? authorizedC3ContainerId)
    {
        ArgumentNullException.ThrowIfNull(discovered);

        List<AITool> tools = [];
        foreach (AITool tool in discovered)
        {
            if (!Repo2C4McpCapabilities.RequiredToolNames.Contains(tool.Name, StringComparer.Ordinal))
            {
                continue;
            }

            if (string.Equals(tool.Name, "generate_likec4", StringComparison.Ordinal))
            {
                if (tool is not AIFunction function)
                {
                    continue;
                }

                tools.Add(new PreviewOnlyMcpFunction(function, authorizedC3ContainerId));
                continue;
            }

            tools.Add(tool);
        }

        return tools;
    }

    private sealed class PreviewOnlyMcpFunction(
        AIFunction inner,
        string? authorizedC3ContainerId)
        : DelegatingAIFunction(inner)
    {
        public override string Description =>
            authorizedC3ContainerId is null
                ? "Preview deterministic LikeC4. This Agent wrapper forces dryRun=true, write=false, ignores destinationPath and disables C3."
                : "Preview deterministic LikeC4. This Agent wrapper forces dryRun=true, write=false, ignores destinationPath and permits C3 only for the user-selected container.";

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(arguments);

            AIFunctionArguments safeArguments = [];
            bool requestedC3 = false;
            foreach ((string key, object? value) in arguments)
            {
                if (key is "dryRun" or "write" or "destinationPath")
                {
                    continue;
                }

                if (key == "c3ContainerId")
                {
                    requestedC3 = value is not null;
                    continue;
                }

                safeArguments[key] = value;
            }

            safeArguments["dryRun"] = true;
            safeArguments["write"] = false;
            safeArguments["destinationPath"] = null;
            safeArguments["c3ContainerId"] =
                requestedC3 && authorizedC3ContainerId is not null
                    ? authorizedC3ContainerId
                    : null;

            return base.InvokeCoreAsync(safeArguments, cancellationToken);
        }
    }
}
