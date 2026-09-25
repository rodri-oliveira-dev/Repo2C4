namespace Repo2C4.Mcp;

/// <summary>Minimal stdio-safe entrypoint; MCP transport is added in phase 3.</summary>
public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Error);

    public static int Run(string[] args, TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardError);

        if (args is ["--help"] or ["-h"])
        {
            standardError.WriteLine("Repo2C4 MCP host: protocol transport is not available yet.");
            return 0;
        }

        standardError.WriteLine("Repo2C4 MCP transport is not implemented yet; refusing to start.");
        return 2;
    }
}
