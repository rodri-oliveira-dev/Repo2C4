using Xunit;

namespace Repo2C4.Mcp.Tests;

public sealed class McpHostTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpUsesOnlyStandardError(string argument)
    {
        using StringWriter error = new();

        int exitCode = Program.Run([argument], error);

        Assert.Equal(0, exitCode);
        Assert.Contains("not available yet", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new string[0])]
    [InlineData(new[] { "--unknown" })]
    public void HostDoesNotClaimToServeMcpYet(string[] arguments)
    {
        using StringWriter error = new();

        int exitCode = Program.Run(arguments, error);

        Assert.Equal(2, exitCode);
        Assert.Contains("refusing to start", error.ToString(), StringComparison.Ordinal);
    }
}
