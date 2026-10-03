using Repo2C4.Core.Contracts;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class SemanticC3ContractTests
{
    [Fact]
    public void ProposalRoundTripIsCanonicalAndDeterministic()
    {
        SemanticC3Proposal proposal = CreateProposal();
        SemanticC3Proposal reordered = proposal with
        {
            SelectedContainerIds = [.. proposal.SelectedContainerIds.Reverse()],
            Components = [.. proposal.Components.Reverse()],
            Relations = [.. proposal.Relations.Reverse()],
        };

        string canonical = SemanticC3ContractJson.Serialize(proposal);
        string reorderedJson = SemanticC3ContractJson.Serialize(reordered);
        SemanticC3Proposal restored = SemanticC3ContractJson.Deserialize(canonical);

        Assert.Equal(canonical, reorderedJson);
        Assert.Equal(canonical, SemanticC3ContractJson.Serialize(restored));
        Assert.Equal(
            ["el_ingestion_api", "el_worker"],
            restored.SelectedContainerIds);
    }

    [Fact]
    public void TaxonomyUsesStableSemanticCategoryNames()
    {
        SemanticC3ComponentCategory[] categories = Enum.GetValues<SemanticC3ComponentCategory>();
        SemanticC3ComponentCandidate[] components =
        [
            .. categories.Select((category, index) => new SemanticC3ComponentCandidate(
                "cmp_" + index,
                "el_ingestion_api",
                category,
                category.ToString(),
                "Architecturally relevant responsibility.",
                ["ev_" + index],
                null,
                ReviewStatus.RequiresReview,
                "Semantic boundary requires review.")),
        ];

        SemanticC3Proposal proposal = new(
            SemanticC3ContractSchema.Version,
            ["el_ingestion_api"],
            [.. components],
            []);

        string json = SemanticC3ContractJson.Serialize(proposal);

        Assert.Contains("\"category\": \"httpEndpoint\"", json, StringComparison.Ordinal);
        Assert.Contains("\"category\": \"applicationService\"", json, StringComparison.Ordinal);
        Assert.Contains("\"category\": \"backgroundWorker\"", json, StringComparison.Ordinal);
        Assert.Contains("\"category\": \"messagingConsumer\"", json, StringComparison.Ordinal);
        Assert.Contains("\"category\": \"messagingPublisher\"", json, StringComparison.Ordinal);
        Assert.Contains("\"category\": \"persistenceAdapter\"", json, StringComparison.Ordinal);
        Assert.Contains("\"category\": \"integrationAdapter\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceSymbolIdentityIsStableAndLineIndependent()
    {
        const string projectPath = "src/Ingestion.Api/Ingestion.Api.csproj";
        const string symbolId = "M:Ingestion.Api.Endpoints.MapIngestion(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)";

        string first = StableIds.ForSemanticC3SourceSymbol(projectPath, symbolId);
        string repeated = StableIds.ForSemanticC3SourceSymbol(projectPath, symbolId);

        Assert.Equal(first, repeated);
        Assert.StartsWith("sym_", first, StringComparison.Ordinal);
        Assert.NotEqual(
            first,
            StableIds.ForSemanticC3SourceSymbol(
                projectPath,
                "M:Ingestion.Api.Endpoints.MapHealth(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)"));
    }

    [Fact]
    public void ContractRepresentsInternalAndExternalRelations()
    {
        SemanticC3Proposal proposal = CreateProposal();

        Assert.Empty(SemanticC3Validator.Validate(proposal));
        Assert.Contains(
            proposal.Relations,
            relation => relation.DestinationKind == SemanticC3RelationTargetKind.Component);
        Assert.Contains(
            proposal.Relations,
            relation => relation.DestinationKind == SemanticC3RelationTargetKind.ArchitectureElement);
    }

    [Fact]
    public void InvalidScopeAndSourceSymbolIdentityAreRejected()
    {
        SemanticC3Proposal proposal = CreateProposal();
        SemanticC3ComponentCandidate first = proposal.Components[0] with
        {
            ContainerId = "el_not_selected",
            SourceSymbol = proposal.Components[0].SourceSymbol! with
            {
                Id = "sym_wrong",
            },
        };

        SemanticC3Proposal invalid = proposal with
        {
            Components =
            [
                first,
                .. proposal.Components.Skip(1),
            ],
        };

        IReadOnlyList<ContractError> errors = SemanticC3Validator.Validate(invalid);

        Assert.Contains(errors, error => error.Code == "semanticC3.containerScope");
        Assert.Contains(errors, error => error.Code == "semanticC3.sourceSymbolId");
    }

    [Fact]
    public void ExistingV1ModelJsonRemainsIndependentFromSemanticC3()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MappingFixtures", "acme.c2.v1.json");
        ArchitectureModel model = ContractJson.DeserializeModel(File.ReadAllText(path));

        string json = ContractJson.SerializeModel(model);

        Assert.DoesNotContain("selectedContainerIds", json, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceSymbol", json, StringComparison.Ordinal);
        Assert.DoesNotContain("httpEndpoint", json, StringComparison.Ordinal);
        Assert.Equal(json, ContractJson.SerializeModel(ContractJson.DeserializeModel(json)));
    }

    private static SemanticC3Proposal CreateProposal()
    {
        const string containerId = "el_ingestion_api";
        SemanticC3SourceSymbolIdentity endpointSymbol = new(
            StableIds.ForSemanticC3SourceSymbol(
                "src/Ingestion.Api/Ingestion.Api.csproj",
                "M:Ingestion.Api.Endpoints.MapIngestion(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)"),
            "src/Ingestion.Api/Ingestion.Api.csproj",
            "M:Ingestion.Api.Endpoints.MapIngestion(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)");

        SemanticC3SourceSymbolIdentity serviceSymbol = new(
            StableIds.ForSemanticC3SourceSymbol(
                "src/Ingestion.Application/Ingestion.Application.csproj",
                "T:Ingestion.Application.IngestEventHandler"),
            "src/Ingestion.Application/Ingestion.Application.csproj",
            "T:Ingestion.Application.IngestEventHandler");

        SemanticC3ComponentCandidate endpoint = new(
            StableIds.ForSemanticC3Component(
                containerId,
                SemanticC3ComponentCategory.HttpEndpoint,
                endpointSymbol.Id),
            containerId,
            SemanticC3ComponentCategory.HttpEndpoint,
            "HTTP ingestion endpoint",
            "Accepts ingestion requests and maps them into the application flow.",
            ["ev_http", "ev_route"],
            endpointSymbol,
            ReviewStatus.RequiresReview,
            "Endpoint boundary is evidence-backed but remains a proposal.");

        SemanticC3ComponentCandidate application = new(
            StableIds.ForSemanticC3Component(
                containerId,
                SemanticC3ComponentCategory.ApplicationService,
                serviceSymbol.Id),
            containerId,
            SemanticC3ComponentCategory.ApplicationService,
            "Idempotent ingestion use case",
            "Coordinates idempotent event ingestion.",
            ["ev_handler"],
            serviceSymbol,
            ReviewStatus.RequiresReview,
            "Application responsibility requires review.");

        SemanticC3RelationCandidate internalRelation = new(
            StableIds.ForRelation(endpoint.Id, application.Id, "semantic-c3|dispatch"),
            endpoint.Id,
            application.Id,
            SemanticC3RelationTargetKind.Component,
            "Dispatches ingestion",
            ["ev_dispatch"],
            ReviewStatus.RequiresReview,
            "Static collaboration evidence does not independently prove runtime flow.");

        SemanticC3RelationCandidate externalRelation = new(
            StableIds.ForRelation(application.Id, "el_postgresql", "semantic-c3|persistence"),
            application.Id,
            "el_postgresql",
            SemanticC3RelationTargetKind.ArchitectureElement,
            "Persists ingestion state",
            ["ev_persistence"],
            ReviewStatus.RequiresReview,
            "External target is an existing C1/C2 element; runtime flow remains reviewable.");

        return new SemanticC3Proposal(
            SemanticC3ContractSchema.Version,
            ["el_worker", containerId],
            [application, endpoint],
            [externalRelation, internalRelation]);
    }
}
