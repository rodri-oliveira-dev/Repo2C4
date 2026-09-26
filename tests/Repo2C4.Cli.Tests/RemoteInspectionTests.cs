using Xunit;

namespace Repo2C4.Cli.Tests;

public sealed class RemoteInspectionTests
{
    [Fact]
    public async Task InspectRejectsDisallowedRemoteSchemeWithoutNetworkAccess()
    {
        using TemporaryDirectory temp = new();
        using StringWriter output = new();
        using StringWriter error = new();

        int exit = await Program.RunAsync(
            [
                "inspect",
                "--remote-url",
                "ssh://example.invalid/repository.git",
                "--output",
                Path.Combine(temp.Path, "snapshot.json"),
            ],
            output,
            error,
            CancellationToken.None);

        Assert.Equal(CliExitCodes.IoError, exit);
        Assert.Contains("remote_url_invalid", error.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(temp.Path, "snapshot.json")));
    }

    [Fact]
    public async Task InspectRequiresExactlyOneLocalOrRemoteSource()
    {
        using TemporaryDirectory temp = new();
        using StringWriter output = new();
        using StringWriter error = new();

        int exit = await Program.RunAsync(
            [
                "inspect",
                "--repository",
                temp.Path,
                "--remote-url",
                "https://example.invalid/repository.git",
                "--output",
                Path.Combine(temp.Path, "snapshot.json"),
            ],
            output,
            error,
            CancellationToken.None);

        Assert.Equal(CliExitCodes.UsageError, exit);
        Assert.Contains("exactly one source", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteRefRequiresRemoteUrl()
    {
        using TemporaryDirectory temp = new();
        using StringWriter output = new();
        using StringWriter error = new();

        int exit = await Program.RunAsync(
            [
                "inspect",
                "--repository",
                temp.Path,
                "--remote-ref",
                "refs/heads/main",
                "--output",
                Path.Combine(temp.Path, "snapshot.json"),
            ],
            output,
            error,
            CancellationToken.None);

        Assert.Equal(CliExitCodes.UsageError, exit);
        Assert.Contains("--remote-ref requires --remote-url", error.ToString(), StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("repo2c4-remote-cli-").FullName;
        }

        public string Path
        {
            get;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
