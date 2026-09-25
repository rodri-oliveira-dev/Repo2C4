using Xunit;

namespace Repo2C4.Cli.Tests;

public sealed class CliHostTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpUsesStandardOutput(string argument)
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = Program.Run([argument], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("Repo2C4 CLI", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData(new string[0])]
    [InlineData(new[] { "inspect" })]
    public void UnimplementedCommandsReturnError(string[] arguments)
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exitCode = Program.Run(arguments, output, error);

        Assert.Equal(2, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("not available yet", error.ToString(), StringComparison.Ordinal);
    }
}
