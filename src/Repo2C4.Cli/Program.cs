namespace Repo2C4.Cli;

/// <summary>Minimal CLI entrypoint; commands are added in later phases.</summary>
public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    public static int Run(string[] args, TextWriter standardOutput, TextWriter standardError)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (args is ["--help"] or ["-h"])
        {
            standardOutput.WriteLine("Repo2C4 CLI: inspection and generation are not available yet.");
            return 0;
        }

        standardError.WriteLine("No CLI commands are available yet. Run --help for the current status.");
        return 2;
    }
}
