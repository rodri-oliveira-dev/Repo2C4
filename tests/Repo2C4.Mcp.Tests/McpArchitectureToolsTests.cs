using Xunit;

namespace Repo2C4.Mcp.Tests;

public sealed class McpArchitectureToolsTests
{
    [Fact]
    public async Task SameNamedDirectoriesAtDifferentRelativePathsGetDifferentRepositoryIds()
    {
        using TemporaryDirectory root = new();
        string first = CreateRepository(root.Path, "a", "App");
        string second = CreateRepository(root.Path, "b", "App");
        using McpSnapshotStore store = new();
        McpArchitectureTools tools = new(root.Path, store);

        McpInspectRepositoryResult firstResult = await tools.InspectRepository(
            Path.GetRelativePath(root.Path, first),
            cancellationToken: TestContext.Current.CancellationToken);
        McpInspectRepositoryResult secondResult = await tools.InspectRepository(
            Path.GetRelativePath(root.Path, second),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(firstResult.RepositoryId, secondResult.RepositoryId);
    }

    [Fact]
    public async Task InspectionImportsExternalEvidenceIntoTheSessionSnapshot()
    {
        using TemporaryDirectory root = new();
        string repository = CreateIntegrationRepository(root.Path);
        using McpSnapshotStore store = new();
        McpArchitectureTools tools = new(root.Path, store);

        McpInspectRepositoryResult result = await tools.InspectRepository(
            Path.GetRelativePath(root.Path, repository),
            "inspection.json",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(result.EvidenceCategories, item => item.Category == "external.http.outbound" && item.Count == 1);
        McpEvidencePageResult page = tools.GetEvidence(
            result.SnapshotId,
            category: "external.http.outbound",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Serasa", Assert.Single(page.Items).Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulExternalImportPreservesDiagnosticsInTheSessionSnapshot()
    {
        using TemporaryDirectory root = new();
        string repository = CreateIntegrationRepository(root.Path);
        string reportPath = Path.Combine(repository, "inspection.json");
        File.WriteAllText(
            reportPath,
            File.ReadAllText(reportPath).Replace(
                "\"truncated\": false",
                "\"truncated\": true",
                StringComparison.Ordinal));
        using McpSnapshotStore store = new();
        McpArchitectureTools tools = new(root.Path, store);

        McpInspectRepositoryResult result = await tools.InspectRepository(
            Path.GetRelativePath(root.Path, repository),
            "inspection.json",
            cancellationToken: TestContext.Current.CancellationToken);
        McpSnapshotPageResult diagnostics = tools.GetSnapshot(
            result.SnapshotId,
            section: "diagnostics",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(result.DiagnosticCount, diagnostics.TotalMatched);
        Assert.Contains(diagnostics.Diagnostics, item => item.Code == "external.discovery.truncated");
        Assert.Contains(diagnostics.Diagnostics, item => item.Code == "external.correlation.pathOnly");
    }

    [Fact]
    public async Task InspectionRejectsExternalReportOutsideSelectedRepository()
    {
        using TemporaryDirectory root = new();
        string repository = CreateRepository(root.Path, "inside", "App");
        File.WriteAllText(Path.Combine(root.Path, "outside.json"), "{}");
        using McpSnapshotStore store = new();
        McpArchitectureTools tools = new(root.Path, store);

        ModelContextProtocol.McpException exception = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(
            () => tools.InspectRepository(
                Path.GetRelativePath(root.Path, repository),
                "../outside.json",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("integration_report_unauthorized", exception.Message, StringComparison.Ordinal);
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

    private static string CreateIntegrationRepository(string root)
    {
        string path = Path.Combine(root, "repository");
        string project = Path.Combine(path, "src", "App");
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(project, "SerasaClient.cs"), "internal sealed class SerasaClient { }");
        File.Copy(
            Path.Combine(AppContext.BaseDirectory, "ExternalFixtures", "dotnetrepoinspector-v1.6.5-canonical.json"),
            Path.Combine(path, "inspection.json"));
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
