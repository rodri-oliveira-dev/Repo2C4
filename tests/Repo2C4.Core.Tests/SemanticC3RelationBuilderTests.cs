using Repo2C4.Core.C3;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.Review;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class SemanticC3RelationBuilderTests
{
    private const string ProjectPath = "src/App/App.csproj";
    private const string ContainerId = "el_app";

    [Fact]
    public void EndpointToUseCaseToPersistenceIsConfirmedWhenWiringAndInvocationConverge()
    {
        SemanticC3ComponentCandidate endpoint = Component(
            "T:Sample.OrdersController",
            SemanticC3ComponentCategory.HttpEndpoint,
            "Orders HTTP endpoint");
        SemanticC3ComponentCandidate application = Component(
            "T:Sample.OrderService",
            SemanticC3ComponentCategory.ApplicationService,
            "Order application service");
        SemanticC3ComponentCandidate persistence = Component(
            "T:Sample.OrderRepository",
            SemanticC3ComponentCategory.PersistenceAdapter,
            "Order repository persistence");

        SemanticC3SourceSymbolIdentity controllerCtor =
            Symbol("M:Sample.OrdersController.#ctor(IOrderService)");
        SemanticC3SourceSymbolIdentity controllerAction =
            Symbol("M:Sample.OrdersController.Create()");
        SemanticC3SourceSymbolIdentity serviceCtor =
            Symbol("M:Sample.OrderService.#ctor(IOrderRepository)");
        SemanticC3SourceSymbolIdentity serviceMethod =
            Symbol("M:Sample.OrderService.Execute()");
        SemanticC3SourceSymbolIdentity program =
            Symbol("M:<global>.Program.<top-level>()");

        SemanticC3FactSet facts = FactSet(
            Fact(controllerCtor, SemanticC3FactKind.ConstructorInjection,
                "semantic.wiring.constructorInjection", "T:IOrderService:0"),
            Fact(program, SemanticC3FactKind.DependencyInjectionRegistration,
                "semantic.wiring.diRegistration",
                "DI:Scoped:IOrderService->OrderService:0"),
            Fact(controllerAction, SemanticC3FactKind.SymbolInvocation,
                "semantic.collaboration.staticInvocation",
                "M:Sample.OrderService.Execute"),
            Fact(serviceCtor, SemanticC3FactKind.ConstructorInjection,
                "semantic.wiring.constructorInjection", "T:IOrderRepository:0"),
            Fact(program, SemanticC3FactKind.DependencyInjectionRegistration,
                "semantic.wiring.diRegistration",
                "DI:Scoped:IOrderRepository->OrderRepository:1"),
            Fact(serviceMethod, SemanticC3FactKind.SymbolInvocation,
                "semantic.collaboration.staticInvocation",
                "M:Sample.OrderRepository.Save"));

        SemanticC3RelationBuildResult result = SemanticC3RelationBuilder.Build(
            BaseModel(),
            Proposal(endpoint, application, persistence),
            facts);

        Assert.Equal(2, result.Proposal.Relations.Length);
        SemanticC3RelationCandidate endpointRelation = Assert.Single(
            result.Proposal.Relations.Where(relation =>
                relation.SourceComponentId == endpoint.Id));
        SemanticC3RelationCandidate persistenceRelation = Assert.Single(
            result.Proposal.Relations.Where(relation =>
                relation.SourceComponentId == application.Id));

        Assert.Equal(application.Id, endpointRelation.DestinationId);
        Assert.Equal(persistence.Id, persistenceRelation.DestinationId);
        Assert.Equal(ReviewStatus.Confirmed, endpointRelation.Status);
        Assert.Equal(ReviewStatus.Confirmed, persistenceRelation.Status);
        Assert.Contains("Delegates HTTP processing", endpointRelation.Description, StringComparison.Ordinal);
        Assert.Contains("for persistence", persistenceRelation.Description, StringComparison.Ordinal);
        Assert.Null(endpointRelation.ReviewReason);
        Assert.Null(persistenceRelation.ReviewReason);
    }

    [Fact]
    public void DiWithoutInvocationAndInvocationWithoutDiRemainReviewable()
    {
        SemanticC3ComponentCandidate endpoint = Component(
            "T:Sample.OrdersController",
            SemanticC3ComponentCategory.HttpEndpoint,
            "Orders HTTP endpoint");
        SemanticC3ComponentCandidate application = Component(
            "T:Sample.OrderService",
            SemanticC3ComponentCategory.ApplicationService,
            "Order application service");
        SemanticC3SourceSymbolIdentity program =
            Symbol("M:<global>.Program.<top-level>()");

        SemanticC3FactSet wiringOnly = FactSet(
            Fact(Symbol("M:Sample.OrdersController.#ctor(IOrderService)"),
                SemanticC3FactKind.ConstructorInjection,
                "semantic.wiring.constructorInjection",
                "T:IOrderService:0"),
            Fact(program, SemanticC3FactKind.DependencyInjectionRegistration,
                "semantic.wiring.diRegistration",
                "DI:Scoped:IOrderService->OrderService:0"));

        SemanticC3RelationCandidate wiringRelation = Assert.Single(
            SemanticC3RelationBuilder.Build(
                BaseModel(),
                Proposal(endpoint, application),
                wiringOnly).Proposal.Relations);
        Assert.Equal(ReviewStatus.RequiresReview, wiringRelation.Status);
        Assert.Contains("no direct symbol invocation", wiringRelation.ReviewReason!, StringComparison.Ordinal);

        SemanticC3FactSet invocationOnly = FactSet(
            Fact(Symbol("M:Sample.OrdersController.Create()"),
                SemanticC3FactKind.SymbolInvocation,
                "semantic.collaboration.staticInvocation",
                "M:Sample.OrderService.Execute"));

        SemanticC3RelationCandidate invocationRelation = Assert.Single(
            SemanticC3RelationBuilder.Build(
                BaseModel(),
                Proposal(endpoint, application),
                invocationOnly).Proposal.Relations);
        Assert.Equal(ReviewStatus.RequiresReview, invocationRelation.Status);
        Assert.Contains("without supporting DI", invocationRelation.ReviewReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void InterfaceImplementationCanResolveInjectedAbstractionWithoutInventingByName()
    {
        SemanticC3ComponentCandidate endpoint = Component(
            "T:Sample.OrdersController",
            SemanticC3ComponentCategory.HttpEndpoint,
            "Orders HTTP endpoint");
        SemanticC3ComponentCandidate application = Component(
            "T:Sample.OrderService",
            SemanticC3ComponentCategory.ApplicationService,
            "Order application service");

        SemanticC3Fact implementation = Fact(
            application.SourceSymbol!,
            SemanticC3FactKind.ImplementedInterface,
            "semantic.symbol.interface",
            "I:IOrderService:0");
        SemanticC3Fact injection = Fact(
            Symbol("M:Sample.OrdersController.#ctor(IOrderService)"),
            SemanticC3FactKind.ConstructorInjection,
            "semantic.wiring.constructorInjection",
            "T:IOrderService:0");

        SemanticC3RelationBuildResult result = SemanticC3RelationBuilder.Build(
            BaseModel(),
            Proposal(endpoint, application),
            FactSet(implementation, injection));

        SemanticC3RelationCandidate relation = Assert.Single(result.Proposal.Relations);
        Assert.Equal(application.Id, relation.DestinationId);
        Assert.Equal(ReviewStatus.RequiresReview, relation.Status);
        Assert.Contains(implementation.Id, relation.EvidenceIds);
        Assert.Contains(injection.Id, relation.EvidenceIds);
    }

    [Fact]
    public void WorkerToPublisherIsConfirmedOnlyWithWiringAndInvocation()
    {
        SemanticC3ComponentCandidate worker = Component(
            "T:Sample.OutboxWorker",
            SemanticC3ComponentCategory.BackgroundWorker,
            "Outbox poller");
        SemanticC3ComponentCandidate publisher = Component(
            "T:Sample.RabbitPublisher",
            SemanticC3ComponentCategory.MessagingPublisher,
            "RabbitMQ publisher adapter");
        SemanticC3SourceSymbolIdentity program =
            Symbol("M:<global>.Program.<top-level>()");

        SemanticC3FactSet facts = FactSet(
            Fact(Symbol("M:Sample.OutboxWorker.#ctor(IEventPublisher)"),
                SemanticC3FactKind.ConstructorInjection,
                "semantic.wiring.constructorInjection",
                "T:IEventPublisher:0"),
            Fact(program, SemanticC3FactKind.DependencyInjectionRegistration,
                "semantic.wiring.diRegistration",
                "DI:Singleton:IEventPublisher->RabbitPublisher:0"),
            Fact(Symbol("M:Sample.OutboxWorker.ExecuteAsync()"),
                SemanticC3FactKind.SymbolInvocation,
                "semantic.collaboration.staticInvocation",
                "M:Sample.RabbitPublisher.Publish"));

        SemanticC3RelationCandidate relation = Assert.Single(
            SemanticC3RelationBuilder.Build(
                BaseModel(),
                Proposal(worker, publisher),
                facts).Proposal.Relations);

        Assert.Equal(ReviewStatus.Confirmed, relation.Status);
        Assert.Equal(publisher.Id, relation.DestinationId);
        Assert.Equal("Publishes through RabbitMQ publisher adapter", relation.Description);
    }

    [Fact]
    public void ConsumerToProcessorToPersistenceCanBeRepresentedWithoutRemoteInference()
    {
        SemanticC3ComponentCandidate consumer = Component(
            "T:Sample.OrderConsumer",
            SemanticC3ComponentCategory.MessagingConsumer,
            "RabbitMQ consumer adapter");
        SemanticC3ComponentCandidate processor = Component(
            "T:Sample.InboxProcessor",
            SemanticC3ComponentCategory.ApplicationService,
            "Inbox processor");
        SemanticC3ComponentCandidate persistence = Component(
            "T:Sample.InboxDbContext",
            SemanticC3ComponentCategory.PersistenceAdapter,
            "Inbox persistence");
        SemanticC3SourceSymbolIdentity program =
            Symbol("M:<global>.Program.<top-level>()");

        SemanticC3FactSet facts = FactSet(
            Fact(Symbol("M:Sample.OrderConsumer.#ctor(IInboxProcessor)"),
                SemanticC3FactKind.ConstructorInjection,
                "semantic.wiring.constructorInjection",
                "T:IInboxProcessor:0"),
            Fact(program, SemanticC3FactKind.DependencyInjectionRegistration,
                "semantic.wiring.diRegistration",
                "DI:Scoped:IInboxProcessor->InboxProcessor:0"),
            Fact(Symbol("M:Sample.OrderConsumer.Handle()"),
                SemanticC3FactKind.SymbolInvocation,
                "semantic.collaboration.staticInvocation",
                "M:Sample.InboxProcessor.Process"),
            Fact(Symbol("M:Sample.InboxProcessor.#ctor(InboxDbContext)"),
                SemanticC3FactKind.ConstructorInjection,
                "semantic.wiring.constructorInjection",
                "T:InboxDbContext:0"),
            Fact(Symbol("M:Sample.InboxProcessor.Process()"),
                SemanticC3FactKind.SymbolInvocation,
                "semantic.collaboration.staticInvocation",
                "M:Sample.InboxDbContext.SaveChanges"));

        SemanticC3RelationBuildResult result = SemanticC3RelationBuilder.Build(
            BaseModel(),
            Proposal(consumer, processor, persistence),
            facts);

        Assert.Equal(2, result.Proposal.Relations.Length);
        Assert.Contains(result.Proposal.Relations, relation =>
            relation.SourceComponentId == consumer.Id &&
            relation.DestinationId == processor.Id &&
            relation.Status == ReviewStatus.Confirmed);
        Assert.Contains(result.Proposal.Relations, relation =>
            relation.SourceComponentId == processor.Id &&
            relation.DestinationId == persistence.Id &&
            relation.Status == ReviewStatus.Confirmed);
        Assert.All(result.Proposal.Relations, relation =>
            Assert.Equal(SemanticC3RelationTargetKind.Component, relation.DestinationKind));
    }

    [Fact]
    public void ExistingConfirmedExternalRelationIsBoundToTheCorrectAdapterOnly()
    {
        ArchitectureModel model = BaseModelWithExternal();
        SemanticC3ComponentCandidate application = Component(
            "T:Sample.OrderService",
            SemanticC3ComponentCategory.ApplicationService,
            "Order application service");
        SemanticC3ComponentCandidate adapter = Component(
            "T:Sample.SerasaClient",
            SemanticC3ComponentCategory.IntegrationAdapter,
            "Serasa integration adapter",
            ["ev_external"]);

        SemanticC3RelationBuildResult result = SemanticC3RelationBuilder.Build(
            model,
            Proposal(application, adapter),
            FactSet());

        SemanticC3RelationCandidate relation = Assert.Single(result.Proposal.Relations);
        Assert.Equal(adapter.Id, relation.SourceComponentId);
        Assert.Equal("el_serasa", relation.DestinationId);
        Assert.Equal(SemanticC3RelationTargetKind.ArchitectureElement, relation.DestinationKind);
        Assert.Equal(ReviewStatus.Confirmed, relation.Status);
        Assert.DoesNotContain(result.Proposal.Relations, item =>
            item.SourceComponentId == application.Id);
    }

    [Fact]
    public void HandlerDelegateCanSupplyEndpointOwnershipButStillNeedsInvocationForConfirmation()
    {
        SemanticC3ComponentCandidate endpoint = Component(
            "M:<global>.Program.<top-level>()",
            SemanticC3ComponentCategory.HttpEndpoint,
            "Orders HTTP endpoint");
        SemanticC3ComponentCandidate application = Component(
            "T:Sample.OrderService",
            SemanticC3ComponentCategory.ApplicationService,
            "Order application service");
        SemanticC3SourceSymbolIdentity handler =
            Symbol("M:Sample.OrderEndpoints.Handle(IOrderService)");
        SemanticC3SourceSymbolIdentity program =
            endpoint.SourceSymbol!;

        SemanticC3Fact handlerFact = Fact(
            program,
            SemanticC3FactKind.EndpointHandler,
            "semantic.host.endpointHandler",
            "M:OrderEndpoints.Handle");
        SemanticC3Fact handlerMethod = Fact(
            handler,
            SemanticC3FactKind.MethodDeclaration,
            "semantic.symbol.method",
            null);
        SemanticC3Fact parameter = Fact(
            handler,
            SemanticC3FactKind.MethodParameter,
            "semantic.symbol.parameter",
            "T:IOrderService:0");
        SemanticC3Fact registration = Fact(
            Symbol("M:<global>.Program.Configure()"),
            SemanticC3FactKind.DependencyInjectionRegistration,
            "semantic.wiring.diRegistration",
            "DI:Scoped:IOrderService->OrderService:0");

        SemanticC3RelationCandidate relation = Assert.Single(
            SemanticC3RelationBuilder.Build(
                BaseModel(),
                Proposal(endpoint, application),
                FactSet(handlerFact, handlerMethod, parameter, registration))
            .Proposal.Relations);

        Assert.Equal(endpoint.Id, relation.SourceComponentId);
        Assert.Equal(application.Id, relation.DestinationId);
        Assert.Equal(ReviewStatus.RequiresReview, relation.Status);
        Assert.Contains(handlerFact.Id, relation.EvidenceIds);
    }

    [Fact]
    public void ReciprocalCodeReferencesDoNotFabricateUnsupportedArchitecturalCycle()
    {
        SemanticC3ComponentCandidate application = Component(
            "T:Sample.OrderService",
            SemanticC3ComponentCategory.ApplicationService,
            "Order application service");
        SemanticC3ComponentCandidate adapter = Component(
            "T:Sample.ExternalAdapter",
            SemanticC3ComponentCategory.IntegrationAdapter,
            "External integration adapter");

        SemanticC3FactSet facts = FactSet(
            Fact(Symbol("M:Sample.OrderService.Execute()"),
                SemanticC3FactKind.SymbolInvocation,
                "semantic.collaboration.staticInvocation",
                "M:Sample.ExternalAdapter.Call"),
            Fact(Symbol("M:Sample.ExternalAdapter.Callback()"),
                SemanticC3FactKind.SymbolInvocation,
                "semantic.collaboration.staticInvocation",
                "M:Sample.OrderService.Handle"));

        SemanticC3RelationBuildResult result = SemanticC3RelationBuilder.Build(
            BaseModel(),
            Proposal(application, adapter),
            facts);

        SemanticC3RelationCandidate relation = Assert.Single(result.Proposal.Relations);
        Assert.Equal(application.Id, relation.SourceComponentId);
        Assert.Equal(adapter.Id, relation.DestinationId);
        Assert.Equal(ReviewStatus.RequiresReview, relation.Status);
        Assert.DoesNotContain(result.Proposal.Relations, item =>
            item.SourceComponentId == adapter.Id &&
            item.DestinationId == application.Id);
    }

    [Fact]
    public void DuplicateSignalsCollapseIntoOneDeterministicEdge()
    {
        SemanticC3ComponentCandidate application = Component(
            "T:Sample.OrderService",
            SemanticC3ComponentCategory.ApplicationService,
            "Order application service");
        SemanticC3ComponentCandidate persistence = Component(
            "T:Sample.OrderRepository",
            SemanticC3ComponentCategory.PersistenceAdapter,
            "Order persistence");

        SemanticC3Fact first = Fact(
            Symbol("M:Sample.OrderService.Execute()"),
            SemanticC3FactKind.SymbolInvocation,
            "semantic.collaboration.staticInvocation",
            "M:Sample.OrderRepository.Save");
        SemanticC3Fact second = Fact(
            Symbol("M:Sample.OrderService.Retry()"),
            SemanticC3FactKind.SymbolInvocation,
            "semantic.collaboration.staticInvocation",
            "M:Sample.OrderRepository.Save");

        SemanticC3RelationBuildResult forward = SemanticC3RelationBuilder.Build(
            BaseModel(),
            Proposal(application, persistence),
            FactSet(first, second));
        SemanticC3RelationBuildResult reverse = SemanticC3RelationBuilder.Build(
            BaseModel(),
            Proposal(application, persistence),
            FactSet(second, first));

        SemanticC3RelationCandidate relation = Assert.Single(forward.Proposal.Relations);
        Assert.Equal(2, relation.EvidenceIds.Length);
        Assert.Equal(
            SemanticC3ContractJson.Serialize(forward.Proposal),
            SemanticC3ContractJson.Serialize(reverse.Proposal));
    }

    [Fact]
    public void ProjectReferenceAloneNeverCreatesConfirmedRuntimeRelation()
    {
        ArchitectureModel model = BaseModel();
        Evidence projectReference = new(
            "ev_project_ref",
            "dotnet.project.reference",
            ProjectPath,
            1,
            EvidenceSourceType.ProjectFile,
            "Project reference exists.");
        model = model with
        {
            Snapshot = model.Snapshot with
            {
                Evidence = [.. model.Snapshot.Evidence, projectReference],
            },
        };

        SemanticC3ComponentCandidate application = Component(
            "T:Sample.OrderService",
            SemanticC3ComponentCategory.ApplicationService,
            "Order application service",
            ["ev_project_ref"]);
        SemanticC3ComponentCandidate persistence = Component(
            "T:Sample.OrderRepository",
            SemanticC3ComponentCategory.PersistenceAdapter,
            "Order persistence");

        SemanticC3RelationBuildResult result = SemanticC3RelationBuilder.Build(
            model,
            Proposal(application, persistence),
            FactSet());

        Assert.Empty(result.Proposal.Relations);
    }

    [Fact]
    public void RelationBudgetTruncatesDeterministicallyAndReportsDiagnostic()
    {
        SemanticC3ComponentCandidate endpoint = Component(
            "T:Sample.OrdersController",
            SemanticC3ComponentCategory.HttpEndpoint,
            "Orders HTTP endpoint");
        SemanticC3ComponentCandidate application = Component(
            "T:Sample.OrderService",
            SemanticC3ComponentCategory.ApplicationService,
            "Order application service");
        SemanticC3ComponentCandidate persistence = Component(
            "T:Sample.OrderRepository",
            SemanticC3ComponentCategory.PersistenceAdapter,
            "Order persistence");

        SemanticC3FactSet facts = FactSet(
            Fact(Symbol("M:Sample.OrdersController.Create()"),
                SemanticC3FactKind.SymbolInvocation,
                "semantic.collaboration.staticInvocation",
                "M:Sample.OrderService.Execute"),
            Fact(Symbol("M:Sample.OrderService.Execute()"),
                SemanticC3FactKind.SymbolInvocation,
                "semantic.collaboration.staticInvocation",
                "M:Sample.OrderRepository.Save"));

        SemanticC3RelationBuildOptions options = new()
        {
            MaxComponents = 3,
            MaxRelations = 1,
        };

        SemanticC3RelationBuildResult first = SemanticC3RelationBuilder.Build(
            BaseModel(),
            Proposal(endpoint, application, persistence),
            facts,
            options);
        SemanticC3RelationBuildResult repeated = SemanticC3RelationBuilder.Build(
            BaseModel(),
            Proposal(endpoint, application, persistence),
            facts,
            options);

        Assert.Single(first.Proposal.Relations);
        Assert.Contains(first.Diagnostics, diagnostic =>
            diagnostic.Code == "semanticC3.relationBudget");
        Assert.Equal(
            SemanticC3ContractJson.Serialize(first.Proposal),
            SemanticC3ContractJson.Serialize(repeated.Proposal));
    }

    [Fact]
    public void SemanticEvidenceReportExplainsSignalsMissingSignalsAndReviewReasons()
    {
        SemanticC3ComponentCandidate endpoint = Component(
            "T:Sample.OrdersController",
            SemanticC3ComponentCategory.HttpEndpoint,
            "Orders HTTP endpoint");
        SemanticC3ComponentCandidate application = Component(
            "T:Sample.OrderService",
            SemanticC3ComponentCategory.ApplicationService,
            "Order application service");

        SemanticC3Fact injection = Fact(
            Symbol("M:Sample.OrdersController.#ctor(IOrderService)"),
            SemanticC3FactKind.ConstructorInjection,
            "semantic.wiring.constructorInjection",
            "T:IOrderService:0");
        SemanticC3Fact registration = Fact(
            Symbol("M:<global>.Program.<top-level>()"),
            SemanticC3FactKind.DependencyInjectionRegistration,
            "semantic.wiring.diRegistration",
            "DI:Scoped:IOrderService->OrderService:0");
        SemanticC3FactSet facts = FactSet(injection, registration);

        SemanticC3RelationBuildResult graph = SemanticC3RelationBuilder.Build(
            BaseModel(),
            Proposal(endpoint, application),
            facts);
        EvidenceReportResult report = EvidenceReportGenerator.GenerateSemanticC3(
            BaseModel(),
            graph,
            facts);

        Assert.Equal("semantic-c3-evidence-report.md", report.FileName);
        Assert.Contains("## Component candidates", report.Content, StringComparison.Ordinal);
        Assert.Contains("## Relations", report.Content, StringComparison.Ordinal);
        Assert.Contains("semantic.wiring.constructorInjection", report.Content, StringComparison.Ordinal);
        Assert.Contains("missing signals: direct symbol invocation", report.Content, StringComparison.Ordinal);
        Assert.Contains("Review:", report.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", report.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", report.Content, StringComparison.Ordinal);
    }

    private static SemanticC3Proposal Proposal(
        params SemanticC3ComponentCandidate[] components) =>
        new(
            SemanticC3ContractSchema.Version,
            [ContainerId],
            [.. components],
            []);

    private static SemanticC3ComponentCandidate Component(
        string symbolId,
        SemanticC3ComponentCategory category,
        string name,
        string[]? evidenceIds = null)
    {
        SemanticC3SourceSymbolIdentity symbol = Symbol(symbolId);
        return new SemanticC3ComponentCandidate(
            StableIds.ForSemanticC3Component(
                ContainerId,
                category,
                symbol.Id),
            ContainerId,
            category,
            name,
            "Test responsibility.",
            evidenceIds is null ? ["fact_component_" + symbol.Id] : [.. evidenceIds],
            symbol,
            ReviewStatus.RequiresReview,
            "Candidate boundary requires architectural review.");
    }

    private static SemanticC3SourceSymbolIdentity Symbol(string symbolId) =>
        new(
            StableIds.ForSemanticC3SourceSymbol(ProjectPath, symbolId),
            ProjectPath,
            symbolId);

    private static SemanticC3Fact Fact(
        SemanticC3SourceSymbolIdentity source,
        SemanticC3FactKind kind,
        string category,
        string? related) =>
        new(
            StableIds.ForSemanticC3Fact(
                ProjectPath,
                SourcePath(source.SymbolId),
                source.Id,
                kind,
                category,
                related ?? string.Empty),
            ProjectPath,
            SourcePath(source.SymbolId),
            1,
            source,
            kind,
            category,
            "Bounded structural test fact.",
            related);

    private static string SourcePath(string symbolId)
    {
        if (symbolId.Contains("OrdersController", StringComparison.Ordinal))
        {
            return "src/App/OrdersController.cs";
        }

        if (symbolId.Contains("OrderRepository", StringComparison.Ordinal) ||
            symbolId.Contains("InboxDbContext", StringComparison.Ordinal))
        {
            return "src/App/Persistence.cs";
        }

        if (symbolId.Contains("RabbitPublisher", StringComparison.Ordinal) ||
            symbolId.Contains("ExternalAdapter", StringComparison.Ordinal))
        {
            return "src/App/Adapters.cs";
        }

        if (symbolId.Contains("OrderConsumer", StringComparison.Ordinal))
        {
            return "src/App/Consumer.cs";
        }

        return "src/App/Program.cs";
    }

    private static SemanticC3FactSet FactSet(params SemanticC3Fact[] facts) =>
        new(
            SemanticC3ContractSchema.Version,
            [.. facts],
            []);

    private static ArchitectureModel BaseModel()
    {
        RepositoryFile[] files =
        [
            new("Repo.slnx", 1, null),
            new(ProjectPath, 1, null),
            new("src/App/Program.cs", 1, null),
            new("src/App/OrdersController.cs", 1, null),
            new("src/App/Persistence.cs", 1, null),
            new("src/App/Adapters.cs", 1, null),
            new("src/App/Consumer.cs", 1, null),
        ];
        Evidence[] evidence =
        [
            new(
                "ev_system",
                "solution",
                "Repo.slnx",
                1,
                EvidenceSourceType.Manifest,
                "Solution exists."),
            new(
                "ev_app_project",
                "dotnet.project",
                ProjectPath,
                1,
                EvidenceSourceType.ProjectFile,
                "Application project exists."),
        ];

        return new ArchitectureModel(
            ContractSchema.Version,
            ArchitectureLevel.C2,
            new RepositorySnapshot(
                ContractSchema.Version,
                "semantic-relations-test",
                [.. files],
                [.. evidence],
                []),
            [
                new ArchitectureElement(
                    "el_system",
                    ArchitectureElementKind.SoftwareSystem,
                    "Orders",
                    null,
                    ["ev_system"],
                    ReviewStatus.Confirmed,
                    null),
                new ArchitectureElement(
                    ContainerId,
                    ArchitectureElementKind.Container,
                    "Application",
                    "el_system",
                    ["ev_app_project"],
                    ReviewStatus.Confirmed,
                    null),
            ],
            []);
    }

    private static ArchitectureModel BaseModelWithExternal()
    {
        ArchitectureModel model = BaseModel();
        Evidence externalEvidence = new(
            "ev_external",
            "external.http.outbound",
            "src/App/Adapters.cs",
            1,
            EvidenceSourceType.SourceCode,
            "HTTP dependency exists.");
        ArchitectureElement external = new(
            "el_serasa",
            ArchitectureElementKind.SoftwareSystem,
            "Serasa",
            null,
            ["ev_external"],
            ReviewStatus.Confirmed,
            null);
        ArchitectureRelation relation = new(
            StableIds.ForRelation(
                ContainerId,
                external.Id,
                "serasa-http"),
            ContainerId,
            external.Id,
            "Calls Serasa via HTTP",
            ["ev_external"],
            ReviewStatus.Confirmed,
            null);

        return model with
        {
            Snapshot = model.Snapshot with
            {
                Evidence = [.. model.Snapshot.Evidence, externalEvidence],
                Files =
                [
                    .. model.Snapshot.Files,
                    new RepositoryFile("src/App/Adapters.cs", 1, null),
                ],
            },
            Elements = [.. model.Elements, external],
            Relations = [relation],
        };
    }
}
