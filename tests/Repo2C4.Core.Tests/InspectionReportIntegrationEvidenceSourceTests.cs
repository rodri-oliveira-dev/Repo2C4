using System.Text;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.ExternalIntegrations;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class InspectionReportIntegrationEvidenceSourceTests
{
    private readonly InspectionReportIntegrationEvidenceSource source = new();

    [Fact]
    public async Task ImportsCanonicalV165FixtureAndIgnoresUnrelatedSections()
    {
        await using FileStream report = File.OpenRead(FixturePath("dotnetrepoinspector-v1.6.5-canonical.json"));
        ExternalIntegrationEvidenceResult result = await source.ReadAsync(report, Context(
                "sample-service",
                "0123456789abcdef0123456789abcdef01234567",
                "src/App/App.csproj",
                "src/App/SerasaClient.cs"), cancellationToken: TestContext.Current.CancellationToken);

        ExternalIntegrationEvidence evidence = Assert.Single(result.Evidence);
        Assert.Equal("external.http.outbound", evidence.Category);
        Assert.Equal("integration-6e7ab864fd3b45dc", evidence.OriginalFindingId);
        Assert.Equal("Serasa:BaseUrl", evidence.ConfigurationKey);
        Assert.Equal("ISerasaApi", evidence.Contract);
        Assert.Equal(ExternalIntegrationConfidence.High, evidence.Confidence);
        Assert.Equal("src/App/SerasaClient.cs", evidence.ToRepositoryEvidence().RelativePath);
        Assert.Equal("external.http.outbound", Assert.Single(result.RepositoryEvidence).Category);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public async Task ImportsAllSupportedKindsDeterministicallyWithPathOnlyCorrelation()
    {
        byte[] original = await File.ReadAllBytesAsync(FixturePath("integration-kinds-v1.6.json"), TestContext.Current.CancellationToken);
        ExternalIntegrationImportContext context = Context(
            null,
            null,
            "src/App/App.csproj",
            "src/App/Integrations.cs");

        ExternalIntegrationEvidenceResult first = await source.ReadAsync(new MemoryStream(original), context, cancellationToken: TestContext.Current.CancellationToken);
        ExternalIntegrationEvidenceResult second = await source.ReadAsync(new MemoryStream(original), context, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(6, first.Evidence.Length);
        Assert.Equal(first.Evidence.Select(item => item.Id), second.Evidence.Select(item => item.Id));
        Assert.Equal(
            ["external.cache", "external.database", "external.http.outbound", "external.messaging.consume", "external.messaging.publish", "external.storage"],
            first.Evidence.Select(item => item.Category).Order(StringComparer.Ordinal));
        Assert.Contains(first.Evidence, item => item.Target is null && item.Confidence == ExternalIntegrationConfidence.Low);
        Assert.Contains(first.Diagnostics, item => item.Code == "external.correlation.pathOnly");
    }

    [Fact]
    public async Task FindingOrderDoesNotChangeEvidenceIdsOrOutputOrder()
    {
        string firstFinding = FindingJson(1);
        string secondFinding = FindingJson(2, target: "OtherTarget");
        string single = ReportJson();
        string forwardJson = single.Replace(firstFinding, firstFinding + "," + secondFinding, StringComparison.Ordinal);
        string reverseJson = single.Replace(firstFinding, secondFinding + "," + firstFinding, StringComparison.Ordinal);

        ExternalIntegrationEvidenceResult forward = await ReadJsonAsync(forwardJson);
        ExternalIntegrationEvidenceResult reverse = await ReadJsonAsync(reverseJson);

        Assert.Equal(forward.Evidence.Select(item => item.Id), reverse.Evidence.Select(item => item.Id));
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("1.5")]
    [InlineData("invalid")]
    public async Task RejectsIncompatibleSchemaWithControlledDiagnostic(string version)
    {
        ExternalIntegrationEvidenceResult result = await ReadJsonAsync(ReportJson(schemaVersion: version));

        Assert.Empty(result.Evidence);
        ExternalIntegrationDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("external.schema.incompatible", diagnostic.Code);
        Assert.DoesNotContain(version, diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:/repo/src/App/Integrations.cs")]
    [InlineData("../outside.cs")]
    [InlineData("src/App/../../outside.cs")]
    public async Task RejectsAbsoluteOrTraversalSourcePaths(string sourcePath)
    {
        ExternalIntegrationEvidenceResult result = await ReadJsonAsync(ReportJson(sourcePath: sourcePath));

        Assert.Empty(result.Evidence);
        Assert.Contains(result.Diagnostics, item => item.Code == "external.finding.path");
    }

    [Theory]
    [InlineData("\"sourceBody\":\"class Secret {}\"")]
    [InlineData("\"connectionString\":\"Password=do-not-propagate\"")]
    [InlineData("\"payload\":\"token=do-not-propagate\"")]
    public async Task RejectsForbiddenContentWithoutEchoingIt(string forbiddenProperty)
    {
        ExternalIntegrationEvidenceResult result = await ReadJsonAsync(
            ReportJson(extraFindingProperty: forbiddenProperty));

        Assert.Empty(result.Evidence);
        ExternalIntegrationDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "external.finding.forbiddenField");
        Assert.Equal("external.finding.forbiddenField", diagnostic.Code);
        Assert.DoesNotContain("do-not-propagate", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("class Secret", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsSecretLikeValueInRetainedField()
    {
        ExternalIntegrationEvidenceResult result = await ReadJsonAsync(
            ReportJson(target: "password=do-not-propagate"));

        Assert.Empty(result.Evidence);
        Assert.Contains(result.Diagnostics, item => item.Code == "external.finding.invalid");
    }

    [Fact]
    public async Task RejectsFindingAndDocumentLimitsWithoutPartialImport()
    {
        string twoFindings = ReportJson().Replace(
            "] , \"integrationDiscovery\"",
            "," + FindingJson(2) + "] , \"integrationDiscovery\"",
            StringComparison.Ordinal);
        ExternalIntegrationEvidenceResult findings = await ReadJsonAsync(
            twoFindings,
            new ExternalIntegrationEvidenceReadOptions { MaxFindings = 1 });
        ExternalIntegrationEvidenceResult bytes = await ReadJsonAsync(
            ReportJson(),
            new ExternalIntegrationEvidenceReadOptions { MaxJsonBytes = 32 });

        Assert.Empty(findings.Evidence);
        Assert.Contains(findings.Diagnostics, item => item.Code == "external.findings.limit");
        Assert.Empty(bytes.Evidence);
        Assert.Contains(bytes.Diagnostics, item => item.Code == "external.report.tooLarge");
    }

    [Fact]
    public async Task RejectsIncompleteFindingAndMismatchedRepositoryOrCommit()
    {
        ExternalIntegrationEvidenceResult incomplete = await ReadJsonAsync(
            ReportJson(includeTechnology: false));
        ExternalIntegrationEvidenceResult repository = await ReadJsonAsync(
            ReportJson(),
            context: Context("different-repository", null, "src/App/App.csproj", "src/App/Integrations.cs"));
        ExternalIntegrationEvidenceResult commit = await ReadJsonAsync(
            ReportJson(),
            context: Context(
                "sample-service",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "src/App/App.csproj",
                "src/App/Integrations.cs"));

        Assert.Contains(incomplete.Diagnostics, item => item.Code == "external.finding.incomplete");
        Assert.Contains(repository.Diagnostics, item => item.Code == "external.repository.identityMismatch");
        Assert.Contains(commit.Diagnostics, item => item.Code == "external.repository.commitMismatch");
    }

    [Fact]
    public async Task RejectsFindingWhoseProjectOrSourceIsAbsentFromSnapshot()
    {
        ExternalIntegrationEvidenceResult result = await ReadJsonAsync(
            ReportJson(),
            context: Context("sample-service", null, "src/App/App.csproj"));

        Assert.Empty(result.Evidence);
        Assert.Contains(result.Diagnostics, item => item.Code == "external.repository.mismatch");
    }

    [Fact]
    public async Task PropagatesCancellation()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await source.ReadAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(ReportJson())),
                Context(null, null, "src/App/App.csproj", "src/App/Integrations.cs"),
                cancellationToken: cancellation.Token));
    }

    private async Task<ExternalIntegrationEvidenceResult> ReadJsonAsync(
        string json,
        ExternalIntegrationEvidenceReadOptions? options = null,
        ExternalIntegrationImportContext? context = null) =>
        await source.ReadAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(json)),
            context ?? Context(null, null, "src/App/App.csproj", "src/App/Integrations.cs"),
            options);

    private static ExternalIntegrationImportContext Context(
        string? repositoryName,
        string? commitSha,
        params string[] paths) =>
        new(new RepositorySnapshot(
            ContractSchema.Version,
            "integration_sample",
            [.. paths.Select(path => new RepositoryFile(path, 1, null))],
            [],
            []))
        {
            ExpectedRepositoryName = repositoryName,
            ExpectedCommitSha = commitSha,
        };

    private static string ReportJson(
        string schemaVersion = "1.6",
        string sourcePath = "src/App/Integrations.cs",
        string target = "Serasa",
        bool includeTechnology = true,
        string? extraFindingProperty = null)
    {
        string technology = includeTechnology ? "\"technology\":\"refit\"," : string.Empty;
        string extra = extraFindingProperty is null ? string.Empty : "," + extraFindingProperty;
        return "{\"schemaVersion\":\"" + schemaVersion + "\"," +
            "\"repository\":{\"name\":\"sample-service\",\"commitSha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}," +
            "\"projects\":[{\"path\":\"src/App/App.csproj\"}]," +
            "\"integrations\":[" + FindingJson(1, sourcePath, target, technology, extra) + "] , " +
            "\"integrationDiscovery\":{\"enabled\":true,\"completed\":true,\"truncated\":false}}";
    }

    private static string FindingJson(
        int line,
        string sourcePath = "src/App/Integrations.cs",
        string target = "Serasa",
        string technology = "\"technology\":\"refit\",",
        string extra = "") =>
        "{\"id\":\"integration-0000000000000001\",\"projectPath\":\"src/App/App.csproj\"," +
        "\"kind\":\"http\",\"direction\":\"outbound\"," + technology +
        "\"target\":\"" + target + "\",\"source\":{\"path\":\"" + sourcePath + "\",\"line\":" +
        line.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}," +
        "\"confidence\":\"high\",\"signals\":[\"http:client\"]" + extra + "}";

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "ExternalIntegrationFixtures", fileName);
}
