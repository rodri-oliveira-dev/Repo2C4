using Xunit;

namespace Repo2C4.Mcp.Tests;

public sealed class RepositoryAccessPolicyTests
{
    [Fact]
    public void ParentTraversalIsRejected()
    {
        using TemporaryDirectory repository = new();

        Assert.Throws<UnauthorizedAccessException>(
            () => RepositoryAccessPolicy.ResolveExistingPath(
                repository.Path,
                "../outside.txt",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AbsoluteRequestedPathIsRejected()
    {
        using TemporaryDirectory repository = new();
        using TemporaryDirectory outside = new();

        Assert.Throws<UnauthorizedAccessException>(
            () => RepositoryAccessPolicy.ResolveExistingPath(
                repository.Path,
                outside.Path,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MissingPathIsControlled()
    {
        using TemporaryDirectory repository = new();

        Assert.Throws<DirectoryNotFoundException>(
            () => RepositoryAccessPolicy.ResolveExistingPath(
                repository.Path,
                "missing",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ExternalSymbolicLinkIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using TemporaryDirectory repository = new();
        using TemporaryDirectory outside = new();
        string link = Path.Combine(repository.Path, "external-link");
        Directory.CreateSymbolicLink(link, outside.Path);

        Assert.Throws<UnauthorizedAccessException>(
            () => RepositoryAccessPolicy.ResolveExistingPath(
                repository.Path,
                "external-link",
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CancellationStopsPathResolution()
    {
        using TemporaryDirectory repository = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => RepositoryAccessPolicy.ResolveExistingPath(repository.Path, ".", cancellation.Token));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"repo2c4-mcp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path
        {
            get;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
