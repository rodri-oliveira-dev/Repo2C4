using Repo2C4.Core.Contracts;
using Repo2C4.Core.Review;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class EvidenceReportGeneratorTests
{
    [Fact]
    public void ReportLinksAssertionsToEvidenceWithoutSourceBodiesOrAbsolutePaths()
    {
        ArchitectureModel model = LoadModel("acme.c2.v1.json");

        EvidenceReportResult result = EvidenceReportGenerator.Generate(model);

        Assert.Equal("evidence-report.md", result.FileName);
        Assert.Contains("## Verified facts", result.Content, StringComparison.Ordinal);
        Assert.Contains("## Hypotheses requiring review", result.Content, StringComparison.Ordinal);
        Assert.Contains("ev_", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportListsWarningsAndMissingOrigins()
    {
        Evidence evidence = new(
            "ev_app",
            "dotnet.project.kind",
            "src/App.csproj",
            1,
            EvidenceSourceType.ProjectFile,
            "Static declaration.");
        RepositorySnapshot snapshot = new(
            ContractSchema.Version,
            "repo_test",
            [],
            [evidence],
            [new RepositoryDiagnostic("scan.limit", DiagnosticSeverity.Warning, null, "Limit reached.")]);
        ArchitectureElement element = new(
            "el_app",
            ArchitectureElementKind.SoftwareSystem,
            "App",
            null,
            [evidence.Id],
            ReviewStatus.RequiresReview,
            "Deployment boundary needs review.");
        ArchitectureModel model = new(
            ContractSchema.Version,
            ArchitectureLevel.C1,
            snapshot,
            [element],
            []);

        EvidenceReportResult result = EvidenceReportGenerator.Generate(model);

        Assert.Contains("scan.limit", result.Content, StringComparison.Ordinal);
        Assert.Contains("src/App.csproj", result.Content, StringComparison.Ordinal);
        Assert.Equal(1, result.Summary.MissingOrigins);
    }

    private static ArchitectureModel LoadModel(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MappingFixtures", fileName);
        return ContractJson.DeserializeModel(File.ReadAllText(path));
    }
}
