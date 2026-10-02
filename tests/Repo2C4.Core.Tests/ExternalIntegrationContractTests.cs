using System.Collections.Immutable;
using Repo2C4.Core.ExternalIntegrations;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class ExternalIntegrationContractTests
{
    [Fact]
    public void BoundaryDeclaresInspectionReportCompatibilityAndFiniteDefaults()
    {
        ExternalIntegrationEvidenceReadOptions options = new();

        Assert.Equal("1.6", ExternalIntegrationReportSchema.MinimumVersion);
        Assert.Equal(1, ExternalIntegrationReportSchema.SupportedMajor);
        Assert.Equal(6, ExternalIntegrationReportSchema.MinimumMinor);
        Assert.Equal(8 * 1024 * 1024, options.MaxJsonBytes);
        Assert.Equal(5_000, options.MaxFindings);
        Assert.Equal(32, options.MaxSignalsPerFinding);
        Assert.Equal(512, options.MaxTextLength);
    }

    [Fact]
    public void NormalizedEvidencePreservesRequiredFindingMetadata()
    {
        ExternalIntegrationEvidence evidence = new(
            "ev_external_http",
            "integration-6e7ab864fd3b45dc",
            "src/App/App.csproj",
            ExternalIntegrationKind.Http,
            ExternalIntegrationDirection.Outbound,
            "refit",
            "Serasa",
            null,
            "Serasa:BaseUrl",
            "ISerasaApi",
            "src/App/SerasaClient.cs",
            42,
            ExternalIntegrationConfidence.High,
            ImmutableArray.Create("http:client", "refit:contract"));

        Assert.Equal("src/App/App.csproj", evidence.ProjectPath);
        Assert.Equal("src/App/SerasaClient.cs", evidence.SourcePath);
        Assert.Equal(42, evidence.SourceLine);
        Assert.Equal("Serasa:BaseUrl", evidence.ConfigurationKey);
        Assert.Equal(["http:client", "refit:contract"], evidence.Signals);
    }
}
