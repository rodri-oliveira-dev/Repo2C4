using Repo2C4.Core.Contracts;
using Repo2C4.Core.ExternalIntegrations;
using Xunit;

namespace Repo2C4.Mcp.Tests;

public sealed class McpSemanticC3DogfoodTests
{
    private static readonly string[] SelectedContainers =
    [
        "el_ingestion_api",
        "el_ingestion_outbox_worker",
        "el_consolidation_api",
        "el_consolidation_worker",
    ];

    private static readonly string[] CanonicalContainers =
    [
        "el_consolidation_api",
        "el_consolidation_worker",
        "el_ingestion_api",
        "el_ingestion_outbox_worker",
    ];

    [Fact]
    public async Task RealInspectionFeedsFourContainerSemanticC3WithoutExposingInternalFacts()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "SemanticC3Dogfood");
        using McpSnapshotStore store = new();
        McpArchitectureTools architecture = new(root, store);

        McpInspectRepositoryResult inspected = await architecture.InspectRepository(
            ".",
            "inspection-v1.6.json",
            cancellationToken: TestContext.Current.CancellationToken);

        McpSnapshotStore.SnapshotEntry entry = store.Get(inspected.SnapshotId);
        Assert.NotNull(entry.Snapshot.SemanticC3Facts);
        Assert.NotNull(entry.Snapshot.ExternalIntegrationEvidence);

        RepositorySnapshot publicSnapshot = entry.Snapshot with
        {
            SemanticC3Facts = null,
            ExternalIntegrationEvidence = null,
        };
        ArchitectureModel reviewed = CreateReviewedModel(
            publicSnapshot,
            entry.Snapshot.ExternalIntegrationEvidence!);

        McpLikeC4Tools tools = new(root, store);
        McpGenerateLikeC4Result generated = await tools.GenerateLikeC4(
            inspected.SnapshotId,
            reviewed,
            c3Containers: SelectedContainers,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(generated.DryRun);
        Assert.False(generated.Written);
        Assert.Equal(CanonicalContainers, generated.C3Views.Select(view => view.ContainerId));
        Assert.Equal(4, generated.C3Views.Length);

        McpLikeC4File semanticReport = Assert.Single(
            generated.Files,
            file => file.FileName == "semantic-c3-evidence-report.md");
        Assert.Contains("ApplicationService", semanticReport.Content, StringComparison.Ordinal);
        Assert.Contains("PersistenceAdapter", semanticReport.Content, StringComparison.Ordinal);
        Assert.Contains("MessagingConsumer", semanticReport.Content, StringComparison.Ordinal);
        Assert.Contains("MessagingPublisher", semanticReport.Content, StringComparison.Ordinal);
        Assert.Contains("IntegrationAdapter", semanticReport.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Idempotency-Key", semanticReport.Content, StringComparison.Ordinal);

        McpLikeC4File model = Assert.Single(
            generated.Files,
            file => file.FileName == "model.c4");
        Assert.DoesNotContain("TelemetryNames", model.Content, StringComparison.Ordinal);

        // The model submitted by the client stays on the stable public snapshot surface.
        Assert.Null(reviewed.Snapshot.SemanticC3Facts);
        Assert.Null(reviewed.Snapshot.ExternalIntegrationEvidence);
    }

    private static ArchitectureModel CreateReviewedModel(
        RepositorySnapshot publicSnapshot,
        ExternalIntegrationEvidenceResult structured)
    {
        string Project(string path) => Assert.Single(
            publicSnapshot.Evidence,
            evidence =>
                evidence.Category == "dotnet.project" &&
                evidence.RelativePath == path).Id;

        string External(
            ExternalIntegrationKind kind,
            string technology,
            string projectPath) =>
            Assert.Single(structured.Evidence, evidence =>
                evidence.Kind == kind &&
                evidence.Technology == technology &&
                evidence.ProjectPath == projectPath).Id;

        const string ingestionApiProject = "src/Ingestion.Api/Ingestion.Api.csproj";
        const string ingestionPersistenceProject = "src/Ingestion.Persistence/Ingestion.Persistence.csproj";
        const string outboxProject = "src/Ingestion.Outbox.Worker/Ingestion.Outbox.Worker.csproj";
        const string consolidationApiProject = "src/Consolidation.Api/Consolidation.Api.csproj";
        const string consolidationPersistenceProject = "src/Consolidation.Persistence/Consolidation.Persistence.csproj";
        const string consolidationWorkerProject = "src/Consolidation.Worker/Consolidation.Worker.csproj";

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

        return new ArchitectureModel(
            ContractSchema.Version,
            ArchitectureLevel.C2,
            publicSnapshot,
            [
                new ArchitectureElement(
                    "el_lab",
                    ArchitectureElementKind.SoftwareSystem,
                    "dotnet-observability-lab",
                    null,
                    [],
                    ReviewStatus.RequiresReview,
                    "Fixture scope mirrors the documented dogfood repository."),
                Container("el_ingestion_api", "Ingestion.Api",
                    Project(ingestionApiProject), Project(ingestionPersistenceProject)),
                Container("el_ingestion_outbox_worker", "Ingestion.Outbox.Worker",
                    Project(outboxProject), Project(ingestionPersistenceProject)),
                Container("el_consolidation_api", "Consolidation.Api",
                    Project(consolidationApiProject), Project(consolidationPersistenceProject)),
                Container("el_consolidation_worker", "Consolidation.Worker",
                    Project(consolidationWorkerProject), Project(consolidationPersistenceProject)),
                ExternalElement("el_postgresql", "PostgreSQL", ingestionDb, consolidationDb),
                ExternalElement("el_redis", "Redis", redis),
                ExternalElement("el_rabbitmq", "RabbitMQ", rabbitPublish, rabbitConsume),
            ],
            [
                Relation("rel_ingestion_api_postgresql", "el_ingestion_api", "el_postgresql", "Uses ingestion_db", ingestionDb),
                Relation("rel_ingestion_api_redis", "el_ingestion_api", "el_redis", "Uses Redis idempotency cache", redis),
                Relation("rel_outbox_postgresql", "el_ingestion_outbox_worker", "el_postgresql", "Claims ingestion Outbox", ingestionDb),
                Relation("rel_outbox_rabbitmq", "el_ingestion_outbox_worker", "el_rabbitmq", "Publishes integration events", rabbitPublish),
                Relation("rel_consolidation_api_postgresql", "el_consolidation_api", "el_postgresql", "Reads consolidation_db", consolidationDb),
                Relation("rel_rabbitmq_consolidation_worker", "el_rabbitmq", "el_consolidation_worker", "Delivers integration events", rabbitConsume),
                Relation("rel_consolidation_worker_postgresql", "el_consolidation_worker", "el_postgresql", "Commits Inbox and aggregate", consolidationDb),
            ]);
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
