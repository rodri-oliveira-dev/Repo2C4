using System.Collections.Immutable;
using Repo2C4.Core.C3;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.ExternalIntegrations;
using Repo2C4.Core.Inspection;
using Repo2C4.Core.LikeC4;
using Repo2C4.Core.Review;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class SemanticC3DogfoodTests
{
    private static readonly string[] SelectedContainers =
    [
        "el_ingestion_api",
        "el_ingestion_outbox_worker",
        "el_consolidation_api",
        "el_consolidation_worker",
    ];

    [Fact]
    public async Task DotNetObservabilityGoldenProducesUsefulBoundedSemanticC3()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "SemanticC3Dogfood");
        RepositorySnapshot inspected = RepositoryFactExtractor.Extract(
            new RepositoryScanOptions(root, "semantic_c3_dogfood"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(inspected.SemanticC3Facts);
        Assert.NotEmpty(inspected.SemanticC3Facts!.Facts);

        await using FileStream report = File.OpenRead(
            Path.Combine(root, "inspection-v1.6.json"));
        ExternalIntegrationSnapshotImportResult imported =
            await ExternalIntegrationSnapshotImporter.ImportAsync(
                inspected,
                report,
                cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(imported.Succeeded);
        Assert.NotNull(imported.Snapshot.ExternalIntegrationEvidence);

        // Persistence through the public v1 JSON contract is required for CLI/offline generation.
        RepositorySnapshot roundTripped = ContractJson.DeserializeSnapshot(
            ContractJson.SerializeSnapshot(imported.Snapshot));
        Assert.NotNull(roundTripped.SemanticC3Facts);
        Assert.NotNull(roundTripped.ExternalIntegrationEvidence);

        ArchitectureModel model = CreateReviewedModel(roundTripped);
        ArchitectureC3BuildResult build = ArchitectureC3Builder.BuildManyDetailed(
            model,
            SelectedContainers);

        Assert.NotNull(build.SemanticResult);
        Assert.Equal(4, build.Workspace.Selections.Length);
        Assert.Equal(SelectedContainers, build.Workspace.Selections.Select(item => item.SelectedContainerId));

        SemanticC3Proposal proposal = build.SemanticResult!.Proposal;
        AssertResponsibilities(
            proposal,
            "el_ingestion_api",
            SemanticC3ComponentCategory.HttpEndpoint,
            SemanticC3ComponentCategory.ApplicationService,
            SemanticC3ComponentCategory.PersistenceAdapter,
            SemanticC3ComponentCategory.IntegrationAdapter);
        AssertResponsibilities(
            proposal,
            "el_ingestion_outbox_worker",
            SemanticC3ComponentCategory.BackgroundWorker,
            SemanticC3ComponentCategory.MessagingPublisher,
            SemanticC3ComponentCategory.PersistenceAdapter);
        AssertResponsibilities(
            proposal,
            "el_consolidation_api",
            SemanticC3ComponentCategory.HttpEndpoint,
            SemanticC3ComponentCategory.ApplicationService,
            SemanticC3ComponentCategory.PersistenceAdapter);
        AssertResponsibilities(
            proposal,
            "el_consolidation_worker",
            SemanticC3ComponentCategory.BackgroundWorker,
            SemanticC3ComponentCategory.MessagingConsumer,
            SemanticC3ComponentCategory.ApplicationService,
            SemanticC3ComponentCategory.PersistenceAdapter);

        Assert.All(
            build.Workspace.Selections,
            selection => Assert.InRange(selection.Components.Length, 1, 6));
        Assert.DoesNotContain(
            proposal.Components,
            component => component.Name.Contains("Telemetry", StringComparison.OrdinalIgnoreCase));

        AssertInternalFlow(
            proposal,
            "el_ingestion_api",
            SemanticC3ComponentCategory.HttpEndpoint,
            SemanticC3ComponentCategory.ApplicationService);
        AssertInternalFlow(
            proposal,
            "el_ingestion_api",
            SemanticC3ComponentCategory.ApplicationService,
            SemanticC3ComponentCategory.PersistenceAdapter);
        AssertInternalFlow(
            proposal,
            "el_ingestion_api",
            SemanticC3ComponentCategory.ApplicationService,
            SemanticC3ComponentCategory.IntegrationAdapter);
        AssertInternalFlow(
            proposal,
            "el_ingestion_outbox_worker",
            SemanticC3ComponentCategory.BackgroundWorker,
            SemanticC3ComponentCategory.MessagingPublisher);
        AssertInternalFlow(
            proposal,
            "el_consolidation_api",
            SemanticC3ComponentCategory.HttpEndpoint,
            SemanticC3ComponentCategory.ApplicationService);
        AssertInternalFlow(
            proposal,
            "el_consolidation_api",
            SemanticC3ComponentCategory.ApplicationService,
            SemanticC3ComponentCategory.PersistenceAdapter);
        AssertInternalFlow(
            proposal,
            "el_consolidation_worker",
            SemanticC3ComponentCategory.MessagingConsumer,
            SemanticC3ComponentCategory.ApplicationService);
        AssertInternalFlow(
            proposal,
            "el_consolidation_worker",
            SemanticC3ComponentCategory.ApplicationService,
            SemanticC3ComponentCategory.PersistenceAdapter);

        Assert.Contains(proposal.Relations, relation =>
            relation.DestinationKind == SemanticC3RelationTargetKind.ArchitectureElement &&
            relation.DestinationId == "el_redis" &&
            Component(proposal, relation.SourceComponentId).ContainerId == "el_ingestion_api");
        Assert.DoesNotContain(proposal.Relations, relation =>
            relation.DestinationId == "el_redis" &&
            Component(proposal, relation.SourceComponentId).ContainerId == "el_consolidation_worker");
        Assert.Contains(proposal.Relations, relation =>
            relation.DestinationId == "el_rabbitmq" &&
            Component(proposal, relation.SourceComponentId).ContainerId == "el_ingestion_outbox_worker");
        Assert.Contains(proposal.Relations, relation =>
            relation.DestinationId == "el_rabbitmq" &&
            Component(proposal, relation.SourceComponentId).ContainerId == "el_consolidation_worker");
        Assert.Contains(proposal.Relations, relation =>
            relation.DestinationId == "el_postgresql");

        Dictionary<string, SemanticC3Fact> facts = roundTripped.SemanticC3Facts!.Facts
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (SemanticC3RelationCandidate relation in proposal.Relations.Where(item =>
                     item.Status == ReviewStatus.Confirmed &&
                     item.DestinationKind == SemanticC3RelationTargetKind.Component))
        {
            SemanticC3Fact[] relationFacts =
            [
                .. relation.EvidenceIds
                    .Where(facts.ContainsKey)
                    .Select(id => facts[id]),
            ];
            Assert.Contains(relationFacts, fact =>
                fact.Kind == SemanticC3FactKind.SymbolInvocation);
            Assert.Contains(relationFacts, fact =>
                fact.Kind is SemanticC3FactKind.ConstructorInjection
                    or SemanticC3FactKind.EndpointDependency
                    or SemanticC3FactKind.MethodParameter
                    or SemanticC3FactKind.DependencyInjectionRegistration
                    or SemanticC3FactKind.EndpointHandler);
        }

        IReadOnlyList<LikeC4GeneratedFile> generated =
            LikeC4Emitter.EmitWithC3(build.Workspace);
        string views = Assert.Single(
            generated,
            file => file.FileName == LikeC4Emitter.C3ViewsFileName).Content;
        Assert.Equal(4, views.Split("view c3_", StringSplitOptions.None).Length - 1);

        EvidenceReportResult reportResult =
            EvidenceReportGenerator.GenerateSemanticC3(
                model,
                build.SemanticResult,
                roundTripped.SemanticC3Facts);
        Assert.Contains("semantic.host", reportResult.Content, StringComparison.Ordinal);
        Assert.Contains("semantic.persistence", reportResult.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Idempotency-Key", reportResult.Content, StringComparison.Ordinal);

        ArchitectureC3BuildResult repeated = ArchitectureC3Builder.BuildManyDetailed(
            model,
            SelectedContainers.Reverse());
        Assert.Equal(
            SemanticC3ContractJson.Serialize(build.SemanticResult.Proposal),
            SemanticC3ContractJson.Serialize(repeated.SemanticResult!.Proposal));
    }

    private static void AssertResponsibilities(
        SemanticC3Proposal proposal,
        string containerId,
        params SemanticC3ComponentCategory[] expected)
    {
        SemanticC3ComponentCandidate[] components =
        [
            .. proposal.Components.Where(item => item.ContainerId == containerId),
        ];
        foreach (SemanticC3ComponentCategory category in expected)
        {
            Assert.Contains(components, component => component.Category == category);
        }
    }

    private static void AssertInternalFlow(
        SemanticC3Proposal proposal,
        string containerId,
        SemanticC3ComponentCategory sourceCategory,
        SemanticC3ComponentCategory destinationCategory)
    {
        HashSet<string> sources =
        [
            .. proposal.Components
                .Where(component =>
                    component.ContainerId == containerId &&
                    component.Category == sourceCategory)
                .Select(component => component.Id),
        ];
        HashSet<string> destinations =
        [
            .. proposal.Components
                .Where(component =>
                    component.ContainerId == containerId &&
                    component.Category == destinationCategory)
                .Select(component => component.Id),
        ];

        Assert.Contains(proposal.Relations, relation =>
            relation.DestinationKind == SemanticC3RelationTargetKind.Component &&
            sources.Contains(relation.SourceComponentId) &&
            destinations.Contains(relation.DestinationId));
    }

    private static SemanticC3ComponentCandidate Component(
        SemanticC3Proposal proposal,
        string componentId) =>
        Assert.Single(proposal.Components, component => component.Id == componentId);

    private static ArchitectureModel CreateReviewedModel(RepositorySnapshot snapshot)
    {
        string Project(string path) => Assert.Single(
            snapshot.Evidence,
            evidence =>
                evidence.Category == "dotnet.project" &&
                evidence.RelativePath == path).Id;

        ExternalIntegrationEvidence[] external =
            snapshot.ExternalIntegrationEvidence!.Evidence.ToArray();

        string External(
            ExternalIntegrationKind kind,
            string technology,
            string projectPath) =>
            Assert.Single(external, item =>
                item.Kind == kind &&
                item.Technology == technology &&
                item.ProjectPath == projectPath).Id;

        string ingestionApiProject = "src/Ingestion.Api/Ingestion.Api.csproj";
        string ingestionPersistenceProject = "src/Ingestion.Persistence/Ingestion.Persistence.csproj";
        string outboxProject = "src/Ingestion.Outbox.Worker/Ingestion.Outbox.Worker.csproj";
        string consolidationApiProject = "src/Consolidation.Api/Consolidation.Api.csproj";
        string consolidationPersistenceProject = "src/Consolidation.Persistence/Consolidation.Persistence.csproj";
        string consolidationWorkerProject = "src/Consolidation.Worker/Consolidation.Worker.csproj";

        string ingestionDb = External(
            ExternalIntegrationKind.Database,
            "postgresql",
            ingestionPersistenceProject);
        string redis = External(
            ExternalIntegrationKind.Cache,
            "redis",
            ingestionApiProject);
        string rabbitPublish = External(
            ExternalIntegrationKind.Messaging,
            "rabbitmq",
            outboxProject);
        string consolidationDb = External(
            ExternalIntegrationKind.Database,
            "postgresql",
            consolidationPersistenceProject);
        string rabbitConsume = External(
            ExternalIntegrationKind.Messaging,
            "rabbitmq",
            consolidationWorkerProject);

        ArchitectureElement[] elements =
        [
            new(
                "el_lab",
                ArchitectureElementKind.SoftwareSystem,
                "dotnet-observability-lab",
                null,
                [],
                ReviewStatus.RequiresReview,
                "Fixture scope mirrors the documented dogfood repository."),
            Container(
                "el_ingestion_api",
                "Ingestion.Api",
                Project(ingestionApiProject),
                Project(ingestionPersistenceProject)),
            Container(
                "el_ingestion_outbox_worker",
                "Ingestion.Outbox.Worker",
                Project(outboxProject),
                Project(ingestionPersistenceProject)),
            Container(
                "el_consolidation_api",
                "Consolidation.Api",
                Project(consolidationApiProject),
                Project(consolidationPersistenceProject)),
            Container(
                "el_consolidation_worker",
                "Consolidation.Worker",
                Project(consolidationWorkerProject),
                Project(consolidationPersistenceProject)),
            ExternalElement("el_postgresql", "PostgreSQL", ingestionDb, consolidationDb),
            ExternalElement("el_redis", "Redis", redis),
            ExternalElement("el_rabbitmq", "RabbitMQ", rabbitPublish, rabbitConsume),
        ];

        ArchitectureRelation[] relations =
        [
            Relation("rel_ingestion_api_postgresql", "el_ingestion_api", "el_postgresql", "Uses ingestion_db", ingestionDb),
            Relation("rel_ingestion_api_redis", "el_ingestion_api", "el_redis", "Uses Redis idempotency cache", redis),
            Relation("rel_outbox_postgresql", "el_ingestion_outbox_worker", "el_postgresql", "Claims ingestion Outbox", ingestionDb),
            Relation("rel_outbox_rabbitmq", "el_ingestion_outbox_worker", "el_rabbitmq", "Publishes integration events", rabbitPublish),
            Relation("rel_consolidation_api_postgresql", "el_consolidation_api", "el_postgresql", "Reads consolidation_db", consolidationDb),
            Relation("rel_rabbitmq_consolidation_worker", "el_rabbitmq", "el_consolidation_worker", "Delivers integration events", rabbitConsume),
            Relation("rel_consolidation_worker_postgresql", "el_consolidation_worker", "el_postgresql", "Commits Inbox and aggregate", consolidationDb),
        ];

        return new ArchitectureModel(
            ContractSchema.Version,
            ArchitectureLevel.C2,
            snapshot,
            [.. elements],
            [.. relations]);
    }

    private static ArchitectureElement Container(
        string id,
        string name,
        params string[] evidenceIds) =>
        new(
            id,
            ArchitectureElementKind.Container,
            name,
            "el_lab",
            [.. evidenceIds],
            ReviewStatus.RequiresReview,
            "C2 deployment boundary is reviewed separately from static Semantic C3.");

    private static ArchitectureElement ExternalElement(
        string id,
        string name,
        params string[] evidenceIds) =>
        new(
            id,
            ArchitectureElementKind.SoftwareSystem,
            name,
            null,
            [.. evidenceIds],
            ReviewStatus.RequiresReview,
            "External resource identity is normalized from static integration evidence.");

    private static ArchitectureRelation Relation(
        string id,
        string source,
        string destination,
        string description,
        string evidenceId) =>
        new(
            id,
            source,
            destination,
            description,
            [evidenceId],
            ReviewStatus.RequiresReview,
            "Dogfood runtime relation remains reviewable.");
}
