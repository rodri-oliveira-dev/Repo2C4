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
    public void ReportListsUnmappedExternalEvidenceWithoutItsDescription()
    {
        ArchitectureModel model = LoadModel("acme.c2.v1.json");
        Evidence external = new(
            "ev_external_pending",
            "external.http.outbound",
            "src/Web/Program.cs",
            7,
            EvidenceSourceType.SourceCode,
            "Secret-looking evidence description must not be printed.");
        model = model with
        {
            Snapshot = model.Snapshot with
            {
                Evidence = [.. model.Snapshot.Evidence, external]
            },
        };

        EvidenceReportResult result = EvidenceReportGenerator.Generate(model);

        Assert.Equal(1, result.Summary.UnmappedExternalIntegrations);
        Assert.Contains("external.http.outbound", result.Content, StringComparison.Ordinal);
        Assert.Contains("src/Web/Program.cs:7", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(external.Description, result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportListsWarningsAndUnsupportedReviewItemsWithoutDiagnosticValues()
    {
        RepositorySnapshot snapshot = new(
            ContractSchema.Version,
            "repo_test",
            [],
            [],
            [new RepositoryDiagnostic("scan.limit", DiagnosticSeverity.Warning, null, "secret-token=should-not-leak")]);
        ArchitectureElement element = new(
            "el_app",
            ArchitectureElementKind.SoftwareSystem,
            "App",
            null,
            [],
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
        Assert.Contains("el_app", result.Content, StringComparison.Ordinal);
        Assert.Contains("no supporting evidence", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("should-not-leak", result.Content, StringComparison.Ordinal);
        Assert.Equal(1, result.Summary.MissingOrigins);
    }

    private static ArchitectureModel LoadModel(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MappingFixtures", fileName);
        return ContractJson.DeserializeModel(File.ReadAllText(path));
    }
}
