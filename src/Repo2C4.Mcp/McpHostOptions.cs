namespace Repo2C4.Mcp;

internal sealed record McpHostOptions(string RepositoryRoot)
{
    internal const string RepositoryRootEnvironmentVariable = "REPO2C4_REPOSITORY_ROOT";

    internal static bool TryParse(
        string[] args,
        string? configuredRoot,
        out McpHostOptions? options,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);

        options = null;
        error = null;
        string? commandLineRoot = null;

        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (string.Equals(argument, "--repository-root", StringComparison.Ordinal))
            {
                if (commandLineRoot is not null || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    error = "Repo2C4 MCP configuration error: --repository-root requires exactly one value.";
                    return false;
                }

                commandLineRoot = args[++index];
                continue;
            }

            const string prefix = "--repository-root=";
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (commandLineRoot is not null)
                {
                    error = "Repo2C4 MCP configuration error: --repository-root may be specified only once.";
                    return false;
                }

                commandLineRoot = argument[prefix.Length..];
                if (string.IsNullOrWhiteSpace(commandLineRoot))
                {
                    error = "Repo2C4 MCP configuration error: --repository-root requires exactly one value.";
                    return false;
                }

                continue;
            }

            error = "Repo2C4 MCP configuration error: unsupported argument. Use --help for usage.";
            return false;
        }

        string? repositoryRoot = commandLineRoot ?? configuredRoot;
        if (string.IsNullOrWhiteSpace(repositoryRoot))
        {
            error =
                $"Repo2C4 MCP configuration error: provide --repository-root or {RepositoryRootEnvironmentVariable}.";
            return false;
        }

        options = new McpHostOptions(repositoryRoot);
        return true;
    }
}
