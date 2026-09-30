namespace Repo2C4.Agent.Tests;

internal static class TestPaths
{
    public static string RepositoryRoot()
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

        throw new DirectoryNotFoundException(
            "Could not locate the Repo2C4 repository root.");
    }

    public static string McpServer(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        string configuration = ResolveConfiguration();
        return Path.Combine(
            repositoryRoot,
            "src",
            "Repo2C4.Mcp",
            "bin",
            configuration,
            "net10.0",
            "Repo2C4.Mcp.dll");
    }

    private static string ResolveConfiguration()
    {
        DirectoryInfo outputDirectory = new(
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));

        DirectoryInfo? targetFrameworkDirectory = outputDirectory;
        if (!string.Equals(
                targetFrameworkDirectory.Name,
                "net10.0",
                StringComparison.OrdinalIgnoreCase))
        {
            targetFrameworkDirectory = targetFrameworkDirectory
                .Parents()
                .FirstOrDefault(parent =>
                    string.Equals(
                        parent.Name,
                        "net10.0",
                        StringComparison.OrdinalIgnoreCase));
        }

        string? configuration = targetFrameworkDirectory?.Parent?.Name;
        if (string.IsNullOrWhiteSpace(configuration))
        {
            throw new DirectoryNotFoundException(
                "Could not resolve the active Agent test build configuration.");
        }

        return configuration;
    }

    private static IEnumerable<DirectoryInfo> Parents(this DirectoryInfo directory)
    {
        DirectoryInfo? current = directory;
        while (current is not null)
        {
            yield return current;
            current = current.Parent;
        }
    }
}
