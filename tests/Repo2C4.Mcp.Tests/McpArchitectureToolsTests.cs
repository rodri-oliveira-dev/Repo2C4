using Xunit;

namespace Repo2C4.Mcp.Tests;

public sealed class McpArchitectureToolsTests
{
    [Fact]
    public void SameNamedDirectoriesAtDifferentRelativePathsGetDifferentRepositoryIds()
    {
        using TemporaryDirectory root = new();
        string first = CreateRepository(root.Path, "a", "App");
        string second = CreateRepository(root.Path, "b", "App");
        using McpSnapshotStore store = new();
        McpArchitectureTools tools = new(root.Path, store);

        McpInspectRepositoryResult firstResult = tools.InspectRepository(
            Path.GetRelativePath(root.Path, first),
            cancellationToken: TestContext.Current.CancellationToken);
        McpInspectRepositoryResult secondResult = tools.InspectRepository(
            Path.GetRelativePath(root.Path, second),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(firstResult.RepositoryId, secondResult.RepositoryId);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RepositoryIdentityPathNormalizationRespectsCaseSensitivity(
        bool caseInsensitive,
        bool expectedEqual)
    {
        string first = McpArchitectureTools.NormalizeRepositoryIdentityPath(
            "a/App",
            caseInsensitive);
        string second = McpArchitectureTools.NormalizeRepositoryIdentityPath(
            "A/app",
            caseInsensitive);

        Assert.Equal(expectedEqual, string.Equals(first, second, StringComparison.Ordinal));
    }

    private static string CreateRepository(string root, string parent, string name)
    {
        string path = Path.Combine(root, parent, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(
            Path.Combine(path, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        return path;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("repo2c4-mcp-architecture-test-").FullName;
        }

        internal string Path
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
