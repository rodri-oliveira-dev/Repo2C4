using System.Collections.Immutable;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.ExternalIntegrations;
using Repo2C4.Core.Generation;
using Repo2C4.Core.LikeC4;
using Repo2C4.Core.Review;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class ExternalIntegrationEndToEndTests
{
    [Fact]
    public async Task OfficialV16ShapeProducesDeterministicConservativeC1AndC2()
    {
        RepositorySnapshot snapshot = Snapshot();
        ExternalIntegrationEvidenceResult imported;
        await using (FileStream report = File.OpenRead(FixturePath("inspection-v1.6.json")))
        {
            imported = await new InspectionReportIntegrationEvidenceSource().ReadAsync(
                report,
                new ExternalIntegrationImportContext(snapshot),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Equal(9, imported.Evidence.Length);
        Assert.DoesNotContain(imported.Diagnostics, item => item.Severity == DiagnosticSeverity.Error);
        Assert.Contains(imported.Evidence, item => item.Contract == "IExternalScoreClient" && item.Target is null);
        Assert.Contains(imported.Evidence, item => item.Technology == "masstransit" && item.Target is null);

        ArchitectureModel c1 = ExternalIntegrationArchitectureMapper.Map(
            BaseModel(ArchitectureLevel.C1, snapshot),
            imported,
            "el_loans");
        ArchitectureModel c2 = ExternalIntegrationArchitectureMapper.Map(
            BaseModel(ArchitectureLevel.C2, snapshot),
            imported,
            "el_loans");
        ArchitectureModel repeated = ExternalIntegrationArchitectureMapper.Map(
            BaseModel(ArchitectureLevel.C2, snapshot),
            imported,
            "el_loans");

        AssertGoldenSemantics(c1);
        AssertGoldenSemantics(c2);
        Assert.Contains(c2.Relations, item => item.SourceId == "el_api" && item.Description.Contains("Serasa", StringComparison.Ordinal));
        Assert.Contains(c2.Relations, item => item.SourceId == "el_api" && item.Description.Contains("loan-approved", StringComparison.Ordinal));
        Assert.Contains(c2.Relations, item => item.DestinationId == "el_worker" && item.Description.Contains("payment-approved", StringComparison.Ordinal));
        Assert.Contains(c2.Relations, item => item.SourceId == "el_worker" && item.Description.Contains("loan-documents", StringComparison.Ordinal));
        Assert.Equal(ContractJson.SerializeModel(c2), ContractJson.SerializeModel(repeated));
        Assert.Equal(LikeC4Emitter.Emit(c2), LikeC4Emitter.Emit(repeated));

        EvidenceReportResult review = EvidenceReportGenerator.Generate(c2);
        Assert.Equal(2, review.Summary.UnmappedExternalIntegrations);
        Assert.Contains("external.http.outbound", review.Content, StringComparison.Ordinal);
        Assert.Contains("external.messaging.publish", review.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedC1AndC2PassOfficialLikeC4WhenIntegrationIsEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("REPO2C4_LIKEC4_INTEGRATION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        RepositorySnapshot snapshot = Snapshot();
        await using FileStream report = File.OpenRead(FixturePath("inspection-v1.6.json"));
        ExternalIntegrationEvidenceResult imported =
            await new InspectionReportIntegrationEvidenceSource().ReadAsync(
                report,
                new ExternalIntegrationImportContext(snapshot),
                cancellationToken: TestContext.Current.CancellationToken);

        foreach (ArchitectureLevel level in new[] { ArchitectureLevel.C1, ArchitectureLevel.C2 })
        {
            ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(
                BaseModel(level, snapshot),
                imported,
                "el_loans");
            using TemporaryWorkspace workspace = new();
            foreach (LikeC4GeneratedFile file in LikeC4Emitter.Emit(mapped))
            {
                File.WriteAllText(Path.Combine(workspace.Path, file.FileName), file.Content);
            }

            LikeC4ValidationResult validation = await LikeC4CliValidator.ValidateAsync(
                workspace.Path,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(
                validation.IsValid,
                string.Join("; ", validation.Diagnostics.Select(item => item.Code + ": " + item.Message)));
        }
    }

    private static void AssertGoldenSemantics(ArchitectureModel model)
    {
        Assert.Contains(model.Elements, item => item.Name == "Serasa");
        Assert.DoesNotContain(model.Elements, item => item.Name == "IExternalScoreClient");
        Assert.Contains(model.Elements, item => item.Name == "Google Pub/Sub");
        Assert.Contains(model.Elements, item => item.Name == "Azure Service Bus");
        Assert.Contains(model.Elements, item => item.Name == "LoanDb");
        Assert.Contains(model.Elements, item => item.Name == "LoanCache");
        Assert.Contains(model.Elements, item => item.Name == "loan-documents");
        Assert.Contains(model.Elements, item => item.Name == "AuditDb" && item.Status == ReviewStatus.RequiresReview);
        Assert.DoesNotContain(model.Elements, item => item.Name.Contains("MassTransit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            model.Elements,
            item => item.Kind == ArchitectureElementKind.Container && item.Id is not "el_api" and not "el_worker");
        Assert.DoesNotContain(model.Relations, item => item.Description.Contains("producer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(model.Relations, item => item.Description.Contains("consumer", StringComparison.OrdinalIgnoreCase));

        string serialized = ContractJson.SerializeModel(model);
        Assert.DoesNotContain("ConnectionStrings:LoanDb", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Redis:Connection", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("namespace ExternalIntegrationsE2E", serialized, StringComparison.Ordinal);
    }

    private static ArchitectureModel BaseModel(ArchitectureLevel level, RepositorySnapshot snapshot)
    {
        ImmutableArray<ArchitectureElement> elements = level == ArchitectureLevel.C1
            ? [SystemElement()]
            :
            [
                SystemElement(),
                new ArchitectureElement("el_api", ArchitectureElementKind.Container, "Loans API", "el_loans", ["ev_api"], ReviewStatus.Confirmed, null),
                new ArchitectureElement("el_worker", ArchitectureElementKind.Container, "Payments Worker", "el_loans", ["ev_worker"], ReviewStatus.Confirmed, null),
            ];
        return new ArchitectureModel(ContractSchema.Version, level, snapshot, elements, []);
    }

    private static ArchitectureElement SystemElement() =>
        new("el_loans", ArchitectureElementKind.SoftwareSystem, "Loans", null, ["ev_api"], ReviewStatus.Confirmed, null);

    private static RepositorySnapshot Snapshot() =>
        new(
            ContractSchema.Version,
            "external_integrations_e2e",
            [
                new RepositoryFile("src/Api/Api.csproj", 1, null),
                new RepositoryFile("src/Api/Integrations.cs", 1, null),
                new RepositoryFile("src/Worker/Worker.csproj", 1, null),
                new RepositoryFile("src/Worker/Integrations.cs", 1, null),
            ],
            [
                new Evidence("ev_api", "dotnet.project", "src/Api/Api.csproj", 1, EvidenceSourceType.ProjectFile, "API project."),
                new Evidence("ev_worker", "dotnet.project", "src/Worker/Worker.csproj", 1, EvidenceSourceType.ProjectFile, "Worker project."),
            ],
            []);

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "ExternalIntegrationE2E", fileName);

    private sealed class TemporaryWorkspace : IDisposable
    {
        internal TemporaryWorkspace() =>
            Path = Directory.CreateTempSubdirectory("repo2c4-external-e2e-").FullName;

        internal string Path
        {
            get;
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
