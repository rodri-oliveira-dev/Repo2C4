using Repo2C4.Core.C3;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.ExternalIntegrations;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class SemanticC3WorkerIntegrationProposerTests
{
    private const string ProjectPath = "src/Worker/Worker.csproj";
    private const string ContainerId = "el_worker";

    [Fact]
    public void OutboxWorkerPublisherAndDbContextProduceBoundedComponentsAndExternalRelations()
    {
        ArchitectureModel baseModel = BaseModel();
        SemanticC3SourceSymbolIdentity worker = Symbol("T:Sample.OutboxWorker");
        SemanticC3SourceSymbolIdentity publisher = Symbol("T:Sample.RabbitPublisher");
        SemanticC3SourceSymbolIdentity dbContext = Symbol("T:Sample.OutboxDbContext");

        SemanticC3FactSet facts = FactSet(
            Fact("src/Worker/Worker.cs", worker, SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type", "Declares class symbol OutboxWorker.", null),
            Fact("src/Worker/Worker.cs", worker, SemanticC3FactKind.HostedService,
                "semantic.host.backgroundService", "Background worker boundary.", "hostedService"),
            Fact("src/Worker/Publisher.cs", publisher, SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type", "Declares class symbol RabbitPublisher.", null),
            Fact("src/Worker/Publisher.cs", publisher, SemanticC3FactKind.MessagingCandidate,
                "semantic.messaging.abstraction", "Publisher role signal.", "messaging"),
            Fact("src/Worker/Data.cs", dbContext, SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type", "Declares class symbol OutboxDbContext.", null),
            Fact("src/Worker/Data.cs", dbContext, SemanticC3FactKind.PersistenceCandidate,
                "semantic.persistence.dbContext", "DbContext persistence boundary.", "dbContext"));

        ExternalIntegrationEvidenceResult external = ExternalResult(
            External(
                "ev_publish",
                ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Publish,
                "rabbitmq",
                "orders.exchange",
                "exchange",
                "OrderCreated",
                "src/Worker/Publisher.cs",
                ExternalIntegrationConfidence.High),
            External(
                "ev_database",
                ExternalIntegrationKind.Database,
                ExternalIntegrationDirection.Read,
                "postgresql",
                "OutboxDb",
                "database",
                null,
                "src/Worker/Data.cs",
                ExternalIntegrationConfidence.High));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(
            baseModel,
            external,
            "el_system");

        SemanticC3Proposal proposal = SemanticC3WorkerIntegrationProposer.Propose(
            mapped,
            ContainerId,
            facts,
            external);

        Assert.Contains(proposal.Components, component =>
            component.Category == SemanticC3ComponentCategory.BackgroundWorker &&
            component.Name == "Outbox worker");
        Assert.Contains(proposal.Components, component =>
            component.Category == SemanticC3ComponentCategory.MessagingPublisher &&
            component.Name == "RabbitMQ publisher adapter");
        Assert.Contains(proposal.Components, component =>
            component.Category == SemanticC3ComponentCategory.PersistenceAdapter &&
            component.Name == "Outbox persistence");

        Assert.Contains(proposal.Relations, relation =>
            relation.DestinationKind == SemanticC3RelationTargetKind.ArchitectureElement &&
            relation.Description == "Publishes OrderCreated to exchange orders.exchange");
        Assert.Contains(proposal.Relations, relation =>
            relation.DestinationKind == SemanticC3RelationTargetKind.ArchitectureElement &&
            relation.Description == "Uses PostgreSQL database OutboxDb");
        Assert.All(proposal.Relations, relation =>
            Assert.Equal(ReviewStatus.RequiresReview, relation.Status));
    }

    [Fact]
    public void ConsumerEvidenceProducesConsumerAdapterWithoutInventingRemotePeer()
    {
        ArchitectureModel baseModel = BaseModel();
        SemanticC3SourceSymbolIdentity consumer = Symbol("T:Sample.OrderConsumer");

        SemanticC3FactSet facts = FactSet(
            Fact("src/Worker/Consumer.cs", consumer, SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type", "Declares class symbol OrderConsumer.", null),
            Fact("src/Worker/Consumer.cs", consumer, SemanticC3FactKind.MessagingCandidate,
                "semantic.messaging.abstraction", "Consumer role signal.", "messaging"));

        ExternalIntegrationEvidenceResult external = ExternalResult(
            External(
                "ev_consume",
                ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Consume,
                "azure-servicebus",
                "orders",
                "queue",
                "OrderCreated",
                "src/Worker/Consumer.cs",
                ExternalIntegrationConfidence.High));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(
            baseModel,
            external,
            "el_system");

        SemanticC3Proposal proposal = SemanticC3WorkerIntegrationProposer.Propose(
            mapped,
            ContainerId,
            facts,
            external);

        SemanticC3ComponentCandidate consumerComponent = Assert.Single(proposal.Components);
        Assert.Equal(SemanticC3ComponentCategory.MessagingConsumer, consumerComponent.Category);
        Assert.Equal("Azure Service Bus consumer adapter", consumerComponent.Name);

        SemanticC3RelationCandidate relation = Assert.Single(proposal.Relations);
        Assert.Equal("Consumes OrderCreated from queue orders", relation.Description);
        Assert.Contains(
            mapped.Elements,
            element => element.Id == relation.DestinationId &&
                element.Name == "Azure Service Bus");
        Assert.DoesNotContain(mapped.Elements, element => element.Name == "OrderCreated");
    }

    [Fact]
    public void RepositoryRequiresObservedUseBeforeBecomingPersistenceComponent()
    {
        ArchitectureModel model = BaseModel();
        SemanticC3SourceSymbolIdentity worker = Symbol("T:Sample.OutboxWorker");
        SemanticC3SourceSymbolIdentity constructor = Symbol(
            "M:Sample.OutboxWorker.#ctor(IOrderRepository)");
        SemanticC3SourceSymbolIdentity repository = Symbol("T:Sample.OrderRepository");
        SemanticC3SourceSymbolIdentity program = Symbol("M:<global>.Program.<top-level>()");

        SemanticC3FactSet usedFacts = FactSet(
            Fact("src/Worker/Worker.cs", worker, SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type", "Declares class symbol OutboxWorker.", null),
            Fact("src/Worker/Worker.cs", worker, SemanticC3FactKind.HostedService,
                "semantic.host.backgroundService", "Background worker boundary.", "hostedService"),
            Fact("src/Worker/Worker.cs", constructor, SemanticC3FactKind.ConstructorInjection,
                "semantic.wiring.constructorInjection", "Worker injects repository.", "T:IOrderRepository:0"),
            Fact("src/Worker/Program.cs", program, SemanticC3FactKind.DependencyInjectionRegistration,
                "semantic.wiring.diRegistration", "DI repository registration.",
                "DI:Scoped:IOrderRepository->OrderRepository:0"),
            Fact("src/Worker/Repository.cs", repository, SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type", "Declares class symbol OrderRepository.", null),
            Fact("src/Worker/Repository.cs", repository, SemanticC3FactKind.PersistenceCandidate,
                "semantic.persistence.repositoryImplementation", "Repository implementation signal.", "repository"));

        SemanticC3Proposal used = SemanticC3WorkerIntegrationProposer.Propose(
            model,
            ContainerId,
            usedFacts,
            ExternalResult());

        Assert.Contains(used.Components, component =>
            component.Category == SemanticC3ComponentCategory.PersistenceAdapter &&
            component.Name == "Order repository persistence");

        SemanticC3FactSet unusedFacts = FactSet(
            Fact("src/Worker/Repository.cs", repository, SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type", "Declares class symbol OrderRepository.", null),
            Fact("src/Worker/Repository.cs", repository, SemanticC3FactKind.PersistenceCandidate,
                "semantic.persistence.repositoryImplementation", "Repository implementation signal.", "repository"));

        SemanticC3Proposal unused = SemanticC3WorkerIntegrationProposer.Propose(
            model,
            ContainerId,
            unusedFacts,
            ExternalResult());

        Assert.DoesNotContain(unused.Components, component =>
            component.Category == SemanticC3ComponentCategory.PersistenceAdapter);
    }

    [Fact]
    public void HostedRegistrationCanProduceReviewableWorkerBoundary()
    {
        ArchitectureModel model = BaseModel();
        SemanticC3SourceSymbolIdentity worker = Symbol("T:Sample.PollingWorker");
        SemanticC3SourceSymbolIdentity program = Symbol("M:<global>.Program.<top-level>()");

        SemanticC3FactSet facts = FactSet(
            Fact("src/Worker/Worker.cs", worker, SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type", "Declares class symbol PollingWorker.", null),
            Fact("src/Worker/Program.cs", program, SemanticC3FactKind.HostedService,
                "semantic.wiring.hostedServiceRegistration", "Hosted service registration.",
                "HOST:PollingWorker:0"));

        SemanticC3Proposal proposal = SemanticC3WorkerIntegrationProposer.Propose(
            model,
            ContainerId,
            facts,
            ExternalResult());

        SemanticC3ComponentCandidate component = Assert.Single(proposal.Components);
        Assert.Equal(SemanticC3ComponentCategory.BackgroundWorker, component.Category);
        Assert.Equal(ReviewStatus.RequiresReview, component.Status);
        Assert.Contains("registration-backed", component.ReviewReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameworkOnlyMessagingWithoutTransportDoesNotCreateAdapter()
    {
        ArchitectureModel baseModel = BaseModel();
        SemanticC3SourceSymbolIdentity publisher = Symbol("T:Sample.OrderPublisher");
        SemanticC3FactSet facts = FactSet(
            Fact("src/Worker/Publisher.cs", publisher, SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type", "Declares class symbol OrderPublisher.", null),
            Fact("src/Worker/Publisher.cs", publisher, SemanticC3FactKind.MessagingCandidate,
                "semantic.messaging.abstraction", "Publisher role signal.", "messaging"));

        ExternalIntegrationEvidenceResult external = ExternalResult(
            External(
                "ev_framework_only",
                ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Publish,
                "masstransit",
                "orders",
                "queue",
                "OrderCreated",
                "src/Worker/Publisher.cs",
                ExternalIntegrationConfidence.High));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(
            baseModel,
            external,
            "el_system");

        SemanticC3Proposal proposal = SemanticC3WorkerIntegrationProposer.Propose(
            mapped,
            ContainerId,
            facts,
            external);

        Assert.Empty(proposal.Components);
        Assert.Empty(proposal.Relations);
        Assert.Equal(baseModel.Elements.Length, mapped.Elements.Length);
    }

    [Fact]
    public void DatabaseAndCacheEvidenceWithoutLocalPersistenceDoNotFabricateComponents()
    {
        ArchitectureModel baseModel = BaseModel();
        SemanticC3SourceSymbolIdentity worker = Symbol("T:Sample.OutboxWorker");
        SemanticC3FactSet facts = FactSet(
            Fact("src/Worker/Worker.cs", worker, SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type", "Declares class symbol OutboxWorker.", null),
            Fact("src/Worker/Worker.cs", worker, SemanticC3FactKind.HostedService,
                "semantic.host.backgroundService", "Background worker boundary.", "hostedService"));

        ExternalIntegrationEvidenceResult external = ExternalResult(
            External(
                "ev_db_unlinked",
                ExternalIntegrationKind.Database,
                ExternalIntegrationDirection.Read,
                "postgresql",
                "WorkerDb",
                "database",
                null,
                "src/Worker/DataClient.cs",
                ExternalIntegrationConfidence.High),
            External(
                "ev_cache_unlinked",
                ExternalIntegrationKind.Cache,
                ExternalIntegrationDirection.Read,
                "redis",
                "WorkerCache",
                "cache",
                null,
                "src/Worker/CacheClient.cs",
                ExternalIntegrationConfidence.High));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(
            baseModel,
            external,
            "el_system");

        SemanticC3Proposal proposal = SemanticC3WorkerIntegrationProposer.Propose(
            mapped,
            ContainerId,
            facts,
            external);

        Assert.Single(proposal.Components);
        Assert.Equal(
            SemanticC3ComponentCategory.BackgroundWorker,
            proposal.Components[0].Category);
        Assert.Empty(proposal.Relations);
    }

    [Fact]
    public void MultipleMessagingAdaptersAndLowConfidenceStayDistinctAndReviewable()
    {
        ArchitectureModel baseModel = BaseModel();
        SemanticC3SourceSymbolIdentity publisher = Symbol("T:Sample.OrderPublisher");
        SemanticC3SourceSymbolIdentity consumer = Symbol("T:Sample.OrderConsumer");

        SemanticC3FactSet facts = FactSet(
            Fact("src/Worker/Publisher.cs", publisher, SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type", "Declares class symbol OrderPublisher.", null),
            Fact("src/Worker/Publisher.cs", publisher, SemanticC3FactKind.MessagingCandidate,
                "semantic.messaging.abstraction", "Publisher role signal.", "messaging"),
            Fact("src/Worker/Consumer.cs", consumer, SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type", "Declares class symbol OrderConsumer.", null),
            Fact("src/Worker/Consumer.cs", consumer, SemanticC3FactKind.MessagingCandidate,
                "semantic.messaging.abstraction", "Consumer role signal.", "messaging"));

        ExternalIntegrationEvidenceResult external = ExternalResult(
            External(
                "ev_pub_low",
                ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Publish,
                "rabbitmq",
                "orders.exchange",
                "exchange",
                "OrderCreated",
                "src/Worker/Publisher.cs",
                ExternalIntegrationConfidence.Low),
            External(
                "ev_consume_high",
                ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Consume,
                "rabbitmq",
                "orders.queue",
                "queue",
                "OrderCreated",
                "src/Worker/Consumer.cs",
                ExternalIntegrationConfidence.High));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(
            baseModel,
            external,
            "el_system");

        SemanticC3Proposal proposal = SemanticC3WorkerIntegrationProposer.Propose(
            mapped,
            ContainerId,
            facts,
            external);

        Assert.Equal(
            2,
            proposal.Components.Count(component =>
                component.Category is SemanticC3ComponentCategory.MessagingPublisher
                    or SemanticC3ComponentCategory.MessagingConsumer));
        SemanticC3ComponentCandidate publisherComponent = Assert.Single(
            proposal.Components.Where(component =>
                component.Category == SemanticC3ComponentCategory.MessagingPublisher));
        Assert.Contains("low", publisherComponent.ReviewReason!, StringComparison.OrdinalIgnoreCase);
        Assert.All(proposal.Components, component =>
            Assert.Equal(ReviewStatus.RequiresReview, component.Status));
    }

    [Fact]
    public void OutputIdsNamesAndRelationsAreDeterministic()
    {
        ArchitectureModel baseModel = BaseModel();
        SemanticC3SourceSymbolIdentity worker = Symbol("T:Sample.OutboxWorker");
        SemanticC3SourceSymbolIdentity publisher = Symbol("T:Sample.RabbitPublisher");

        SemanticC3FactSet facts = FactSet(
            Fact("src/Worker/Worker.cs", worker, SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type", "Declares class symbol OutboxWorker.", null),
            Fact("src/Worker/Worker.cs", worker, SemanticC3FactKind.HostedService,
                "semantic.host.backgroundService", "Background worker boundary.", "hostedService"),
            Fact("src/Worker/Publisher.cs", publisher, SemanticC3FactKind.TypeDeclaration,
                "semantic.symbol.type", "Declares class symbol RabbitPublisher.", null),
            Fact("src/Worker/Publisher.cs", publisher, SemanticC3FactKind.MessagingCandidate,
                "semantic.messaging.abstraction", "Publisher role signal.", "messaging"));

        ExternalIntegrationEvidence publish = External(
            "ev_publish",
            ExternalIntegrationKind.Messaging,
            ExternalIntegrationDirection.Publish,
            "rabbitmq",
            "orders.exchange",
            "exchange",
            "OrderCreated",
            "src/Worker/Publisher.cs",
            ExternalIntegrationConfidence.High);
        ExternalIntegrationEvidenceResult external = ExternalResult(publish);
        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(
            baseModel,
            external,
            "el_system");

        SemanticC3Proposal first = SemanticC3WorkerIntegrationProposer.Propose(
            mapped,
            ContainerId,
            facts,
            external);
        SemanticC3Proposal repeated = SemanticC3WorkerIntegrationProposer.Propose(
            mapped,
            ContainerId,
            facts,
            external);

        Assert.Equal(
            SemanticC3ContractJson.Serialize(first),
            SemanticC3ContractJson.Serialize(repeated));
    }

    private static ArchitectureModel BaseModel()
    {
        RepositoryFile[] files =
        [
            new("Repo.slnx", 1, null),
            new(ProjectPath, 1, null),
            new("src/Worker/Worker.cs", 1, null),
            new("src/Worker/Program.cs", 1, null),
            new("src/Worker/Publisher.cs", 1, null),
            new("src/Worker/Consumer.cs", 1, null),
            new("src/Worker/Data.cs", 1, null),
            new("src/Worker/DataClient.cs", 1, null),
            new("src/Worker/CacheClient.cs", 1, null),
            new("src/Worker/Repository.cs", 1, null),
        ];
        Evidence[] evidence =
        [
            new(
                "ev_system",
                "solution",
                "Repo.slnx",
                1,
                EvidenceSourceType.Manifest,
                "Solution is present."),
            new(
                "ev_worker_project",
                "dotnet.project",
                ProjectPath,
                1,
                EvidenceSourceType.ProjectFile,
                "Worker project is present."),
        ];

        return new ArchitectureModel(
            ContractSchema.Version,
            ArchitectureLevel.C2,
            new RepositorySnapshot(
                ContractSchema.Version,
                "semantic_worker_test",
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
                    "Worker",
                    "el_system",
                    ["ev_worker_project"],
                    ReviewStatus.Confirmed,
                    null),
            ],
            []);
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
        string sourcePath,
        SemanticC3SourceSymbolIdentity symbol,
        SemanticC3FactKind kind,
        string category,
        string description,
        string? relatedSymbolId) =>
        new(
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

    private static ExternalIntegrationEvidenceResult ExternalResult(
        params ExternalIntegrationEvidence[] evidence) =>
        new(
            ExternalIntegrationReportSchema.MinimumVersion,
            "semantic-worker-test",
            "abcdef0",
            true,
            false,
            [.. evidence],
            []);

    private static ExternalIntegrationEvidence External(
        string id,
        ExternalIntegrationKind kind,
        ExternalIntegrationDirection direction,
        string technology,
        string target,
        string? resourceType,
        string? contract,
        string sourcePath,
        ExternalIntegrationConfidence confidence)
    {
        string category = (kind, direction) switch
        {
            (ExternalIntegrationKind.Messaging, ExternalIntegrationDirection.Publish) =>
                "external.messaging.publish",
            (ExternalIntegrationKind.Messaging, ExternalIntegrationDirection.Consume) =>
                "external.messaging.consume",
            (ExternalIntegrationKind.Database, _) => "external.database",
            (ExternalIntegrationKind.Cache, _) => "external.cache",
            _ => "external.integration",
        };

        return new ExternalIntegrationEvidence(
            id,
            category,
            "Normalized integration evidence.",
            id,
            ProjectPath,
            kind,
            direction,
            technology,
            target,
            resourceType,
            null,
            contract,
            sourcePath,
            1,
            confidence,
            []);
    }
}
