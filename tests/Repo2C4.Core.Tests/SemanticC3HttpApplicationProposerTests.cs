using Repo2C4.Core.C3;
using Repo2C4.Core.Contracts;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class SemanticC3HttpApplicationProposerTests
{
    private const string ProjectPath = "src/Ingestion.Api/Ingestion.Api.csproj";
    private const string ProgramPath = "src/Ingestion.Api/Program.cs";
    private const string ContainerId = "el_ingestion_api";

    [Fact]
    public void GoldenFixtureProducesUsefulHttpAndApplicationCandidatesDeterministically()
    {
        ArchitectureModel model = LoadGoldenModel();
        SemanticC3SourceSymbolIdentity program = Symbol("M:<global>.Program.<top-level>()");
        SemanticC3SourceSymbolIdentity service = Symbol("T:Ingestion.Api.IngestionService");

        SemanticC3FactSet facts = FactSet(
            Fact(program, SemanticC3FactKind.HttpBoundary, "semantic.host.minimalApi", "MapPost route.", "route:MapPost:0"),
            Fact(program, SemanticC3FactKind.HttpBoundary, "semantic.host.minimalApi", "MapGet route.", "route:MapGet:1"),
            Fact(program, SemanticC3FactKind.EndpointDependency, "semantic.wiring.endpointDependency", "Endpoint depends on IIngestionService.", "T:IIngestionService:0"),
            Fact(program, SemanticC3FactKind.DependencyInjectionRegistration, "semantic.wiring.diRegistration", "DI registration.", "DI:Scoped:IIngestionService->IngestionService:0"),
            Fact(service, SemanticC3FactKind.TypeDeclaration, "semantic.symbol.type", "Declares class symbol IngestionService.", null),
            Fact(Symbol("M:Ingestion.Api.IngestionService.Execute()"), SemanticC3FactKind.MethodDeclaration, "semantic.symbol.method", "Declares method Execute.", null));

        SemanticC3Proposal first = SemanticC3HttpApplicationProposer.Propose(model, ContainerId, facts);
        SemanticC3Proposal repeated = SemanticC3HttpApplicationProposer.Propose(model, ContainerId, facts);

        SemanticC3ComponentCandidate http = Assert.Single(first.Components.Where(component =>
            component.Category == SemanticC3ComponentCategory.HttpEndpoint));
        SemanticC3ComponentCandidate application = Assert.Single(first.Components.Where(component =>
            component.Category == SemanticC3ComponentCategory.ApplicationService));

        Assert.Equal("Ingestion Api HTTP endpoint", http.Name);
        Assert.Equal("Ingestion application service", application.Name);
        Assert.DoesNotContain("HTTP interface", http.Name, StringComparison.Ordinal);
        Assert.Equal(ReviewStatus.RequiresReview, http.Status);
        Assert.Equal(ReviewStatus.RequiresReview, application.Status);
        Assert.Contains(facts.Facts[0].Id, http.EvidenceIds);
        Assert.Contains(facts.Facts[2].Id, application.EvidenceIds);
        Assert.Equal(
            SemanticC3ContractJson.Serialize(first),
            SemanticC3ContractJson.Serialize(repeated));
    }

    [Fact]
    public void ControllerInjectionCanDistinguishTwoApplicationServices()
    {
        ArchitectureModel model = LoadGoldenModel();
        SemanticC3SourceSymbolIdentity controller = Symbol("T:Ingestion.Api.OrdersController");
        SemanticC3SourceSymbolIdentity action = Symbol("M:Ingestion.Api.OrdersController.Create()");
        SemanticC3SourceSymbolIdentity constructor = Symbol("M:Ingestion.Api.OrdersController.#ctor(IOrderService,IAuditHandler)");
        SemanticC3SourceSymbolIdentity orderService = Symbol("T:Ingestion.Api.OrderService");
        SemanticC3SourceSymbolIdentity auditHandler = Symbol("T:Ingestion.Api.AuditHandler");

        SemanticC3FactSet facts = FactSet(
            Fact(controller, SemanticC3FactKind.TypeDeclaration, "semantic.symbol.type", "Declares class symbol OrdersController.", null),
            Fact(controller, SemanticC3FactKind.HttpBoundary, "semantic.host.controller", "Controller boundary.", "controller"),
            Fact(action, SemanticC3FactKind.MethodDeclaration, "semantic.symbol.method", "Declares method Create.", null),
            Fact(action, SemanticC3FactKind.ControllerAction, "semantic.host.controllerAction", "HTTP action.", "httpAction"),
            Fact(constructor, SemanticC3FactKind.ConstructorInjection, "semantic.wiring.constructorInjection", "Depends on IOrderService.", "T:IOrderService:0"),
            Fact(constructor, SemanticC3FactKind.ConstructorInjection, "semantic.wiring.constructorInjection", "Depends on IAuditHandler.", "T:IAuditHandler:1"),
            Fact(Symbol("M:<global>.Program.<top-level>()"), SemanticC3FactKind.DependencyInjectionRegistration, "semantic.wiring.diRegistration", "Registers order service.", "DI:Scoped:IOrderService->OrderService:0"),
            Fact(Symbol("M:<global>.Program.<top-level>()"), SemanticC3FactKind.DependencyInjectionRegistration, "semantic.wiring.diRegistration", "Registers audit handler.", "DI:Scoped:IAuditHandler->AuditHandler:1"),
            Fact(orderService, SemanticC3FactKind.TypeDeclaration, "semantic.symbol.type", "Declares class symbol OrderService.", null),
            Fact(Symbol("M:Ingestion.Api.OrderService.Execute()"), SemanticC3FactKind.MethodDeclaration, "semantic.symbol.method", "Declares method Execute.", null),
            Fact(auditHandler, SemanticC3FactKind.TypeDeclaration, "semantic.symbol.type", "Declares class symbol AuditHandler.", null),
            Fact(Symbol("M:Ingestion.Api.AuditHandler.Handle()"), SemanticC3FactKind.MethodDeclaration, "semantic.symbol.method", "Declares method Handle.", null));

        SemanticC3Proposal proposal = SemanticC3HttpApplicationProposer.Propose(model, ContainerId, facts);

        Assert.Single(proposal.Components.Where(component =>
            component.Category == SemanticC3ComponentCategory.HttpEndpoint));
        SemanticC3ComponentCandidate[] applications =
        [
            .. proposal.Components.Where(component =>
                component.Category == SemanticC3ComponentCategory.ApplicationService),
        ];
        Assert.Equal(2, applications.Length);
        Assert.Contains(applications, component => component.Name == "Order application service");
        Assert.Contains(applications, component => component.Name == "Audit handler");
        Assert.All(applications, component =>
            Assert.Contains("HTTP dependency/handler", component.ReviewReason!, StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitHandlerAndExtensionMethodRegistrationRemainStructurallyLinked()
    {
        ArchitectureModel model = LoadGoldenModel();
        SemanticC3SourceSymbolIdentity program = Symbol("M:<global>.Program.<top-level>()");
        SemanticC3SourceSymbolIdentity handler = Symbol("M:Ingestion.Api.OrderEndpoints.Handle(IOrderService)");
        SemanticC3SourceSymbolIdentity registration = Symbol("M:Ingestion.Api.ServiceCollectionExtensions.AddOrders(IServiceCollection)");
        SemanticC3SourceSymbolIdentity service = Symbol("T:Ingestion.Api.OrderService");

        SemanticC3FactSet facts = FactSet(
            Fact(program, SemanticC3FactKind.HttpBoundary, "semantic.host.minimalApi", "MapPost route.", "route:MapPost:0"),
            Fact(program, SemanticC3FactKind.EndpointHandler, "semantic.host.endpointHandler", "Explicit handler.", "M:OrderEndpoints.Handle"),
            Fact(handler, SemanticC3FactKind.MethodDeclaration, "semantic.symbol.method", "Declares handler.", null),
            Fact(handler, SemanticC3FactKind.MethodParameter, "semantic.symbol.parameter", "Handler parameter.", "T:IOrderService:0"),
            Fact(registration, SemanticC3FactKind.DependencyInjectionRegistration, "semantic.wiring.diRegistration", "Extension registration.", "DI:Scoped:IOrderService->OrderService:0"),
            Fact(service, SemanticC3FactKind.TypeDeclaration, "semantic.symbol.type", "Declares class symbol OrderService.", null),
            Fact(Symbol("M:Ingestion.Api.OrderService.Execute()"), SemanticC3FactKind.MethodDeclaration, "semantic.symbol.method", "Declares method Execute.", null));

        SemanticC3Proposal proposal = SemanticC3HttpApplicationProposer.Propose(model, ContainerId, facts);

        SemanticC3ComponentCandidate application = Assert.Single(proposal.Components.Where(component =>
            component.Category == SemanticC3ComponentCategory.ApplicationService));
        Assert.Equal("Order application service", application.Name);
        Assert.Contains(facts.Facts[1].Id, application.EvidenceIds);
        Assert.Contains(facts.Facts[3].Id, application.EvidenceIds);
        Assert.Contains(facts.Facts[4].Id, application.EvidenceIds);
    }

    [Fact]
    public void ServiceNameWithoutDiOrEndpointUseDoesNotBecomeAComponent()
    {
        ArchitectureModel model = LoadGoldenModel();
        SemanticC3SourceSymbolIdentity program = Symbol("M:<global>.Program.<top-level>()");
        SemanticC3SourceSymbolIdentity lonely = Symbol("T:Ingestion.Api.LonelyService");

        SemanticC3FactSet facts = FactSet(
            Fact(program, SemanticC3FactKind.HttpBoundary, "semantic.host.minimalApi", "MapGet route.", "route:MapGet:0"),
            Fact(lonely, SemanticC3FactKind.TypeDeclaration, "semantic.symbol.type", "Declares class symbol LonelyService.", null),
            Fact(Symbol("M:Ingestion.Api.LonelyService.Execute()"), SemanticC3FactKind.MethodDeclaration, "semantic.symbol.method", "Declares method Execute.", null));

        SemanticC3Proposal proposal = SemanticC3HttpApplicationProposer.Propose(model, ContainerId, facts);

        Assert.Single(proposal.Components);
        Assert.Equal(SemanticC3ComponentCategory.HttpEndpoint, proposal.Components[0].Category);
    }

    [Fact]
    public void MinimalApiWithoutExplicitApplicationLayerStillProducesOnlyHttpBoundary()
    {
        ArchitectureModel model = LoadGoldenModel();
        SemanticC3SourceSymbolIdentity program = Symbol("M:<global>.Program.<top-level>()");

        SemanticC3FactSet facts = FactSet(
            Fact(program, SemanticC3FactKind.HttpBoundary, "semantic.host.minimalApi", "MapGet route.", "route:MapGet:0"),
            Fact(program, SemanticC3FactKind.HttpBoundary, "semantic.host.minimalApi", "MapPost route.", "route:MapPost:1"));

        SemanticC3Proposal proposal = SemanticC3HttpApplicationProposer.Propose(model, ContainerId, facts);

        SemanticC3ComponentCandidate http = Assert.Single(proposal.Components);
        Assert.Equal(SemanticC3ComponentCategory.HttpEndpoint, http.Category);
        Assert.Equal(2, http.EvidenceIds.Length);
        Assert.Empty(proposal.Relations);
    }

    [Fact]
    public void RegisteredTypeInMinimalApiProjectWithoutEndpointUseStaysReviewable()
    {
        ArchitectureModel model = LoadGoldenModel();
        SemanticC3SourceSymbolIdentity program = Symbol("M:<global>.Program.<top-level>()");
        SemanticC3SourceSymbolIdentity service = Symbol("T:Ingestion.Api.OrderService");

        SemanticC3FactSet facts = FactSet(
            Fact(program, SemanticC3FactKind.HttpBoundary, "semantic.host.minimalApi", "MapGet route.", "route:MapGet:0"),
            Fact(program, SemanticC3FactKind.DependencyInjectionRegistration, "semantic.wiring.diRegistration", "DI registration.", "DI:Scoped:IOrderService->OrderService:0"),
            Fact(service, SemanticC3FactKind.TypeDeclaration, "semantic.symbol.type", "Declares class symbol OrderService.", null),
            Fact(Symbol("M:Ingestion.Api.OrderService.Execute()"), SemanticC3FactKind.MethodDeclaration, "semantic.symbol.method", "Declares method Execute.", null));

        SemanticC3Proposal proposal = SemanticC3HttpApplicationProposer.Propose(model, ContainerId, facts);

        SemanticC3ComponentCandidate application = Assert.Single(proposal.Components.Where(component =>
            component.Category == SemanticC3ComponentCategory.ApplicationService));
        Assert.Equal(ReviewStatus.RequiresReview, application.Status);
        Assert.Contains("endpoint-level usage was not observed", application.ReviewReason!, StringComparison.Ordinal);
    }

    private static ArchitectureModel LoadGoldenModel()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "SemanticC3Fixtures",
            "dotnet-observability-lab.ingestion.v1.1.c2.json");
        return ContractJson.DeserializeModel(File.ReadAllText(path));
    }

    private static SemanticC3FactSet FactSet(params SemanticC3Fact[] facts) =>
        new(
            SemanticC3ContractSchema.Version,
            [.. facts],
            []);

    private static SemanticC3SourceSymbolIdentity Symbol(string symbolId) =>
        new(
            StableIds.ForSemanticC3SourceSymbol(ProjectPath, symbolId),
            ProjectPath,
            symbolId);

    private static SemanticC3Fact Fact(
        SemanticC3SourceSymbolIdentity symbol,
        SemanticC3FactKind kind,
        string category,
        string description,
        string? relatedSymbolId)
    {
        string sourcePath = symbol.SymbolId.StartsWith("M:<global>", StringComparison.Ordinal)
            ? ProgramPath
            : "src/Ingestion.Api/SemanticFixture.cs";

        return new SemanticC3Fact(
            StableIds.ForSemanticC3Fact(
                ProjectPath,
                sourcePath,
                symbol.Id,
                kind,
                category,
                relatedSymbolId ?? string.Empty),
            ProjectPath,
            sourcePath,
            1,
            symbol,
            kind,
            category,
            description,
            relatedSymbolId);
    }
}
