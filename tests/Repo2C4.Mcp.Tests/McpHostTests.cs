using Xunit;

namespace Repo2C4.Mcp.Tests;

public sealed class McpHostTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task HelpUsesOnlyDiagnosticChannel(string argument)
    {
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            [argument],
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Repo2C4 MCP server over stdio", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("stdout is reserved exclusively for MCP protocol messages", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownArgumentIsRejectedBeforeTransportStarts()
    {
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--unknown"],
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Contains("unsupported argument", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingRepositoryRootIsControlledWhenNoLocalConfigurationExists()
    {
        const string variable = "REPO2C4_REPOSITORY_ROOT";
        string? original = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, null);
        try
        {
            using StringWriter error = new();

            int exitCode = await Program.RunAsync(
                [],
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(2, exitCode);
            Assert.Contains("provide --repository-root", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
        }
    }

    [Fact]
    public async Task MissingRepositoryDirectoryIsControlled()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"repo2c4-missing-{Guid.NewGuid():N}");
        using StringWriter error = new();

        int exitCode = await Program.RunAsync(
            ["--repository-root", missing],
            error,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.Contains("invalid or unavailable", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(missing, error.ToString(), StringComparison.Ordinal);
    }
}
