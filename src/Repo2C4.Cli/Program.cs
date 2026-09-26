namespace Repo2C4.Cli;

/// <summary>CLI entry point for offline operations and explicit local provider inference.</summary>
public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    public static int Run(string[] args, TextWriter standardOutput, TextWriter standardError) =>
        RunAsync(args, standardOutput, standardError, CancellationToken.None).GetAwaiter().GetResult();

    public static Task<int> RunAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken,
        HttpClient? inferenceClient = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        return CliApplication.RunAsync(args, standardOutput, standardError, cancellationToken, inferenceClient);
    }
}
