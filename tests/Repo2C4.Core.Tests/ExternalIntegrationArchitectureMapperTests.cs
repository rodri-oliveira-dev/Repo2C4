using System.Collections.Immutable;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.ExternalIntegrations;
using Repo2C4.Core.LikeC4;
using Repo2C4.Core.Review;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class ExternalIntegrationArchitectureMapperTests
{
    [Fact]
    public void StrongHttpTargetsCreateTraceableRootDependencies()
    {
        ArchitectureModel model = ModelWithContainers(("el_api", "src/Api/Api.csproj"));
        ExternalIntegrationEvidenceResult imported = Result(
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 10, ExternalIntegrationKind.Http,
                ExternalIntegrationDirection.Outbound, "refit", "Serasa", confidence: ExternalIntegrationConfidence.High),
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 20, ExternalIntegrationKind.Http,
                ExternalIntegrationDirection.Outbound, "httpclient", "Antifraud", confidence: ExternalIntegrationConfidence.High));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(model, imported, "el_system");

        ArchitectureElement serasa = Assert.Single(mapped.Elements, item => item.Name == "Serasa");
        ArchitectureElement antifraud = Assert.Single(mapped.Elements, item => item.Name == "Antifraud");
        Assert.Null(serasa.ParentId);
        Assert.Null(antifraud.ParentId);
        Assert.Equal(ArchitectureElementKind.SoftwareSystem, serasa.Kind);
        Assert.Equal(ReviewStatus.Confirmed, serasa.Status);
        Assert.Contains(mapped.Relations, item =>
            item.SourceId == "el_api" && item.DestinationId == serasa.Id &&
            item.Description == "Calls Serasa via HTTP (Refit)" && item.Status == ReviewStatus.Confirmed);
        Assert.Contains(mapped.Relations, item => item.Description == "Calls Antifraud via HTTP (HttpClient)");
        Assert.All(imported.RepositoryEvidence, item => Assert.Contains(item, mapped.Snapshot.Evidence));
    }

    [Fact]
    public void C1UsesOnlyTheExplicitlySelectedFocalSystemAsOrigin()
    {
        ArchitectureModel c2 = ModelWithContainers(("el_api", "src/Api/Api.csproj"));
        ArchitectureModel c1 = c2 with
        {
            Level = ArchitectureLevel.C1,
            Elements = [Assert.Single(c2.Elements, item => item.Id == "el_system")],
        };
        ExternalIntegrationEvidenceResult imported = Result(
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 10, ExternalIntegrationKind.Http,
                ExternalIntegrationDirection.Outbound, "refit", "Serasa", confidence: ExternalIntegrationConfidence.High));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(c1, imported, "el_system");

        Assert.DoesNotContain(mapped.Elements, item => item.Kind == ArchitectureElementKind.Container);
        ArchitectureRelation relation = Assert.Single(mapped.Relations);
        Assert.Equal("el_system", relation.SourceId);
        Assert.Equal(ReviewStatus.Confirmed, relation.Status);
    }

    [Fact]
    public void HttpWithoutTargetStaysOnlyAsPendingEvidence()
    {
        ArchitectureModel model = ModelWithContainers(("el_api", "src/Api/Api.csproj"));
        ExternalIntegrationEvidenceResult imported = Result(
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 10, ExternalIntegrationKind.Http,
                ExternalIntegrationDirection.Outbound, "httpclient", null, confidence: ExternalIntegrationConfidence.Medium));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(model, imported, "el_system");
        EvidenceReportResult report = EvidenceReportGenerator.Generate(mapped);

        Assert.Equal(
            model.Elements.Select(item => item.Id).Order(StringComparer.Ordinal),
            mapped.Elements.Select(item => item.Id));
        Assert.Equal(model.Relations, mapped.Relations);
        Assert.Equal(1, report.Summary.UnmappedExternalIntegrations);
        Assert.Contains("External integrations pending architectural mapping", report.Content, StringComparison.Ordinal);
        Assert.Contains("external.http.outbound", report.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Serasa", report.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void DataCacheAndStorageUseObservedTargetsWithoutOwnedContainers()
    {
        ArchitectureModel model = ModelWithContainers(("el_api", "src/Api/Api.csproj"));
        ExternalIntegrationEvidenceResult imported = Result(
            Finding("src/Api/Api.csproj", "src/Api/Data.cs", 10, ExternalIntegrationKind.Database,
                ExternalIntegrationDirection.Read, "postgresql", "LoanDb", confidence: ExternalIntegrationConfidence.High),
            Finding("src/Api/Api.csproj", "src/Api/Cache.cs", 20, ExternalIntegrationKind.Cache,
                ExternalIntegrationDirection.Read, "redis", "LoanCache", confidence: ExternalIntegrationConfidence.High),
            Finding("src/Api/Api.csproj", "src/Api/Storage.cs", 30, ExternalIntegrationKind.Storage,
                ExternalIntegrationDirection.Write, "gcs", "loan-documents", "bucket", ExternalIntegrationConfidence.High));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(model, imported, "el_system");

        Assert.Contains(mapped.Relations, item => item.Description == "Uses PostgreSQL database LoanDb");
        Assert.Contains(mapped.Relations, item => item.Description == "Uses Redis cache LoanCache");
        Assert.Contains(mapped.Relations, item => item.Description == "Writes objects to GCS bucket loan-documents");
        Assert.All(
            mapped.Elements.Where(item => item.Name is "LoanDb" or "LoanCache" or "loan-documents"),
            item =>
            {
                Assert.Equal(ArchitectureElementKind.SoftwareSystem, item.Kind);
                Assert.Null(item.ParentId);
            });
    }

    [Fact]
    public void ProjectProvenanceSelectsTheCorrectContainerAndAmbiguityFallsBackForReview()
    {
        ArchitectureModel model = ModelWithContainers(
            ("el_api", "src/Api/Api.csproj"),
            ("el_worker", "src/Worker/Worker.csproj"));
        ExternalIntegrationEvidenceResult workerFinding = Result(
            Finding("src/Worker/Worker.csproj", "src/Worker/Data.cs", 10, ExternalIntegrationKind.Database,
                ExternalIntegrationDirection.Read, "postgresql", "WorkerDb", confidence: ExternalIntegrationConfidence.High));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(model, workerFinding, "el_system");
        Assert.Equal("el_worker", Assert.Single(mapped.Relations).SourceId);

        ArchitectureModel ambiguous = model with
        {
            Elements =
            [
                .. model.Elements.Select(item => item.Kind == ArchitectureElementKind.Container
                    ? item with { EvidenceIds = ["ev_shared_project"] }
                    : item),
            ],
            Snapshot = model.Snapshot with
            {
                Evidence =
                [
                    .. model.Snapshot.Evidence.Where(item => !item.Id.EndsWith("_project", StringComparison.Ordinal)),
                    new Evidence("ev_shared_project", "dotnet.project", "src/Shared/Shared.csproj", 1,
                        EvidenceSourceType.ProjectFile, "MSBuild project manifest is present."),
                ],
                Files = [.. model.Snapshot.Files, new RepositoryFile("src/Shared/Shared.csproj", 1, null),
                    new RepositoryFile("src/Shared/Data.cs", 1, null)],
            },
        };
        ExternalIntegrationEvidenceResult sharedFinding = Result(
            Finding("src/Shared/Shared.csproj", "src/Shared/Data.cs", 10, ExternalIntegrationKind.Database,
                ExternalIntegrationDirection.Read, "postgresql", "SharedDb", confidence: ExternalIntegrationConfidence.High));

        ArchitectureRelation reviewRelation = Assert.Single(
            ExternalIntegrationArchitectureMapper.Map(ambiguous, sharedFinding, "el_system").Relations);
        Assert.Equal("el_system", reviewRelation.SourceId);
        Assert.Equal(ReviewStatus.RequiresReview, reviewRelation.Status);
        Assert.Contains("Multiple containers", reviewRelation.ReviewReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitProjectMappingIsUsedWhenNoContainerEvidenceMatches()
    {
        ArchitectureModel model = ModelWithContainers(("el_api", "src/Api/Api.csproj"));
        ExternalIntegrationEvidenceResult imported = Result(
            Finding("src/Shared/Shared.csproj", "src/Shared/Data.cs", 10, ExternalIntegrationKind.Database,
                ExternalIntegrationDirection.Read, "postgresql", "SharedDb", confidence: ExternalIntegrationConfidence.High));
        model = model with
        {
            Snapshot = model.Snapshot with
            {
                Files =
                [
                    .. model.Snapshot.Files,
                    new RepositoryFile("src/Shared/Shared.csproj", 1, null),
                    new RepositoryFile("src/Shared/Data.cs", 1, null),
                ],
            },
        };
        ExternalIntegrationMappingOptions options = new()
        {
            ProjectOriginElementIds = ImmutableDictionary<string, string>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("src/Shared/Shared.csproj", "el_api"),
        };

        ArchitectureRelation relation = Assert.Single(
            ExternalIntegrationArchitectureMapper.Map(model, imported, "el_system", options).Relations);

        Assert.Equal("el_api", relation.SourceId);
        Assert.Equal(ReviewStatus.Confirmed, relation.Status);
    }

    [Fact]
    public void LowConfidenceAndReviewPendingOriginsCannotBecomeConfirmed()
    {
        ArchitectureModel model = ModelWithContainers(("el_api", "src/Api/Api.csproj"));
        ArchitectureElement api = Assert.Single(model.Elements, item => item.Id == "el_api");
        model = model with
        {
            Elements = [.. model.Elements.Select(item => item.Id == api.Id
                ? item with { Status = ReviewStatus.RequiresReview, ReviewReason = "Container boundary is pending review." }
                : item)],
        };
        ExternalIntegrationEvidenceResult imported = Result(
            Finding("src/Api/Api.csproj", "src/Api/Data.cs", 10, ExternalIntegrationKind.Database,
                ExternalIntegrationDirection.Read, "postgresql", "LoanDb", confidence: ExternalIntegrationConfidence.Low));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(model, imported, "el_system");

        Assert.Equal(ReviewStatus.RequiresReview, Assert.Single(mapped.Elements, item => item.Name == "LoanDb").Status);
        ArchitectureRelation relation = Assert.Single(mapped.Relations);
        Assert.Equal(ReviewStatus.RequiresReview, relation.Status);
        Assert.NotNull(relation.ReviewReason);
    }

    [Theory]
    [InlineData(ExternalIntegrationKind.Database, ExternalIntegrationDirection.Unknown)]
    [InlineData(ExternalIntegrationKind.Cache, ExternalIntegrationDirection.Publish)]
    [InlineData(ExternalIntegrationKind.Storage, ExternalIntegrationDirection.Consume)]
    public void UnknownOrNonsensicalDataDirectionsRequireReview(
        ExternalIntegrationKind kind,
        ExternalIntegrationDirection direction)
    {
        ArchitectureModel model = ModelWithContainers(("el_api", "src/Api/Api.csproj"));
        ExternalIntegrationEvidenceResult imported = Result(
            Finding("src/Api/Api.csproj", "src/Api/Data.cs", 10, kind,
                direction, "postgresql", "ExternalData", confidence: ExternalIntegrationConfidence.High));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(model, imported, "el_system");

        ArchitectureRelation relation = Assert.Single(mapped.Relations);
        Assert.Equal(ReviewStatus.RequiresReview, relation.Status);
        Assert.Contains("direction", relation.ReviewReason, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedTargetCreatesOneExternalSystemAndOneRelationPerCorrelatedProject()
    {
        ArchitectureModel model = ModelWithContainers(
            ("el_api", "src/Api/Api.csproj"),
            ("el_worker", "src/Worker/Worker.csproj"));
        ExternalIntegrationEvidenceResult imported = Result(
            Finding("src/Api/Api.csproj", "src/Api/Data.cs", 10, ExternalIntegrationKind.Database,
                ExternalIntegrationDirection.Read, "postgresql", "SharedDb", confidence: ExternalIntegrationConfidence.High),
            Finding("src/Worker/Worker.csproj", "src/Worker/Data.cs", 10, ExternalIntegrationKind.Database,
                ExternalIntegrationDirection.Read, "postgresql", "SharedDb", confidence: ExternalIntegrationConfidence.High));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(model, imported, "el_system");

        ArchitectureElement target = Assert.Single(mapped.Elements, item => item.Name == "SharedDb");
        Assert.Equal(2, target.EvidenceIds.Length);
        Assert.Equal(2, mapped.Relations.Length);
        Assert.Equal(["el_api", "el_worker"], mapped.Relations.Select(item => item.SourceId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void MessagingPublishAndConsumePreserveProviderResourceContractAndDirection()
    {
        ArchitectureModel model = ModelWithContainers(
            ("el_api", "src/Api/Api.csproj"),
            ("el_worker", "src/Worker/Worker.csproj"));
        ExternalIntegrationEvidenceResult imported = Result(
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 10, ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Publish, "google-pubsub", "loan-approved", "topic",
                ExternalIntegrationConfidence.High, "LoanApproved"),
            Finding("src/Worker/Worker.csproj", "src/Worker/Clients.cs", 20, ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Consume, "azure-servicebus", "payment-approved", "queue",
                ExternalIntegrationConfidence.High, "PaymentApprovedConsumer"));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(model, imported, "el_system");

        ArchitectureElement pubsub = Assert.Single(mapped.Elements, item => item.Name == "Google Pub/Sub");
        ArchitectureElement serviceBus = Assert.Single(mapped.Elements, item => item.Name == "Azure Service Bus");
        Assert.Contains(mapped.Relations, item =>
            item.SourceId == "el_api" && item.DestinationId == pubsub.Id &&
            item.Description == "Publishes LoanApproved to topic loan-approved");
        Assert.Contains(mapped.Relations, item =>
            item.SourceId == serviceBus.Id && item.DestinationId == "el_worker" &&
            item.Description == "Consumes PaymentApprovedConsumer from queue payment-approved");
        Assert.DoesNotContain(mapped.Elements, item => item.Name is "LoanApproved" or "PaymentApprovedConsumer");
    }

    [Fact]
    public void MessagingProviderCatalogDoesNotTurnResourcesIntoContainers()
    {
        ArchitectureModel model = ModelWithContainers(("el_api", "src/Api/Api.csproj"));
        ExternalIntegrationEvidenceResult imported = Result(
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 10, ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Publish, "aws-sqs", "orders", "queue", contract: "OrderCreated"),
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 20, ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Publish, "rabbitmq", "orders.exchange", "exchange", contract: "OrderCreated"),
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 30, ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Consume, "rabbitmq", "orders.queue", "queue", contract: "OrderCreated"),
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 40, ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Publish, "kafka", "orders", "topic", contract: "OrderCreated"));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(model, imported, "el_system");

        Assert.Contains(mapped.Elements, item => item.Name == "Amazon SQS");
        Assert.Contains(mapped.Elements, item => item.Name == "RabbitMQ");
        Assert.Contains(mapped.Elements, item => item.Name == "Kafka");
        Assert.DoesNotContain(mapped.Elements, item => item.Kind == ArchitectureElementKind.Container && item.Id != "el_api");
        Assert.Contains(mapped.Relations, item => item.Description == "Publishes OrderCreated to queue orders");
        Assert.Contains(mapped.Relations, item => item.Description == "Publishes OrderCreated to exchange orders.exchange");
        Assert.Contains(mapped.Relations, item => item.Description == "Consumes OrderCreated from queue orders.queue");
        Assert.Contains(mapped.Relations, item => item.Description == "Publishes OrderCreated to topic orders");
    }

    [Fact]
    public void FrameworkOnlyAndUnknownMessagingRemainPendingEvidence()
    {
        ArchitectureModel model = ModelWithContainers(("el_api", "src/Api/Api.csproj"));
        ExternalIntegrationEvidenceResult imported = Result(
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 10, ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Publish, "masstransit", "orders", "queue", contract: "OrderCreated"),
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 20, ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Consume, "nservicebus", "orders", "queue", contract: "OrderCreated"),
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 30, ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Publish, "unknown", "custom-orders", contract: "OrderCreated"));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(model, imported, "el_system");
        EvidenceReportResult report = EvidenceReportGenerator.Generate(mapped);

        Assert.Equal(model.Elements.Length, mapped.Elements.Length);
        Assert.Empty(mapped.Relations);
        Assert.Equal(3, report.Summary.UnmappedExternalIntegrations);
        Assert.DoesNotContain(mapped.Elements, item => item.Name is "RabbitMQ" or "Azure Service Bus");
    }

    [Fact]
    public void MessagingResourcesAndSharedUseAreDeduplicatedDeterministically()
    {
        ArchitectureModel model = ModelWithContainers(
            ("el_api", "src/Api/Api.csproj"),
            ("el_worker", "src/Worker/Worker.csproj"));
        ExternalIntegrationEvidenceResult imported = Result(
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 10, ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Publish, "google-pubsub", "loan-approved", "topic", contract: "LoanApproved"),
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 20, ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Publish, "google-pubsub", "loan-rejected", "topic", contract: "LoanRejected"),
            Finding("src/Worker/Worker.csproj", "src/Worker/Clients.cs", 30, ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Consume, "google-pubsub", "loan-approved", "topic", contract: "LoanApproved"));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(model, imported, "el_system");

        ArchitectureElement provider = Assert.Single(mapped.Elements, item => item.Name == "Google Pub/Sub");
        Assert.Equal(3, provider.EvidenceIds.Length);
        Assert.Equal(3, mapped.Relations.Length);
        Assert.Contains(mapped.Relations, item => item.SourceId == provider.Id && item.DestinationId == "el_worker");
        Assert.Equal(2, mapped.Relations.Count(item => item.SourceId == "el_api" && item.DestinationId == provider.Id));
    }

    [Fact]
    public void LowConfidenceMessagingRequiresReviewAndMissingTargetIsNotMapped()
    {
        ArchitectureModel model = ModelWithContainers(("el_api", "src/Api/Api.csproj"));
        ExternalIntegrationEvidenceResult imported = Result(
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 10, ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Publish, "aws-sqs", "orders", "queue",
                ExternalIntegrationConfidence.Low, "OrderCreated"),
            Finding("src/Api/Api.csproj", "src/Api/Clients.cs", 20, ExternalIntegrationKind.Messaging,
                ExternalIntegrationDirection.Publish, "google-pubsub", null, "topic",
                ExternalIntegrationConfidence.High, "OrderCreated"));

        ArchitectureModel mapped = ExternalIntegrationArchitectureMapper.Map(model, imported, "el_system");
        EvidenceReportResult report = EvidenceReportGenerator.Generate(mapped);

        ArchitectureRelation relation = Assert.Single(mapped.Relations);
        Assert.Equal(ReviewStatus.RequiresReview, relation.Status);
        Assert.Contains("low confidence", relation.ReviewReason, StringComparison.Ordinal);
        Assert.Equal(1, report.Summary.UnmappedExternalIntegrations);
    }

    [Fact]
    public void MappingAndLikeC4EmissionAreIndependentOfFindingOrder()
    {
        ArchitectureModel model = ModelWithContainers(("el_api", "src/Api/Api.csproj"));
        ExternalIntegrationEvidence first = Finding(
            "src/Api/Api.csproj", "src/Api/Clients.cs", 10, ExternalIntegrationKind.Http,
            ExternalIntegrationDirection.Outbound, "refit", "Serasa", confidence: ExternalIntegrationConfidence.High);
        ExternalIntegrationEvidence second = Finding(
            "src/Api/Api.csproj", "src/Api/Data.cs", 20, ExternalIntegrationKind.Database,
            ExternalIntegrationDirection.Read, "postgresql", "LoanDb", confidence: ExternalIntegrationConfidence.High);

        ArchitectureModel forward = ExternalIntegrationArchitectureMapper.Map(model, Result(first, second), "el_system");
        ArchitectureModel reverse = ExternalIntegrationArchitectureMapper.Map(model, Result(second, first), "el_system");

        Assert.Equal(ContractJson.SerializeModel(forward), ContractJson.SerializeModel(reverse));
        Assert.Equal(LikeC4Emitter.Emit(forward), LikeC4Emitter.Emit(reverse));
    }

    private static ArchitectureModel ModelWithContainers(params (string Id, string ProjectPath)[] containers)
    {
        List<RepositoryFile> files =
        [
            new("Repo.slnx", 1, null),
        ];
        List<Evidence> evidence =
        [
            new("ev_system", "solution", "Repo.slnx", 1, EvidenceSourceType.Manifest, "Solution is present."),
        ];
        List<ArchitectureElement> elements =
        [
            new("el_system", ArchitectureElementKind.SoftwareSystem, "Loans", null, ["ev_system"], ReviewStatus.Confirmed, null),
        ];

        foreach ((string id, string projectPath) in containers)
        {
            string evidenceId = "ev_" + id[3..] + "_project";
            files.Add(new RepositoryFile(projectPath, 1, null));
            string directory = projectPath[..projectPath.LastIndexOf('/')];
            files.Add(new RepositoryFile(directory + "/Clients.cs", 1, null));
            files.Add(new RepositoryFile(directory + "/Data.cs", 1, null));
            files.Add(new RepositoryFile(directory + "/Cache.cs", 1, null));
            files.Add(new RepositoryFile(directory + "/Storage.cs", 1, null));
            evidence.Add(new Evidence(evidenceId, "dotnet.project", projectPath, 1,
                EvidenceSourceType.ProjectFile, "MSBuild project manifest is present."));
            elements.Add(new ArchitectureElement(
                id,
                ArchitectureElementKind.Container,
                id,
                "el_system",
                [evidenceId],
                ReviewStatus.Confirmed,
                null));
        }

        return new ArchitectureModel(
            ContractSchema.Version,
            ArchitectureLevel.C2,
            new RepositorySnapshot(ContractSchema.Version, "mapping_test", [.. files], [.. evidence], []),
            [.. elements],
            []);
    }

    private static ExternalIntegrationEvidenceResult Result(params ExternalIntegrationEvidence[] evidence) =>
        new("1.6", "mapping-test", null, true, false, [.. evidence], []);

    private static ExternalIntegrationEvidence Finding(
        string projectPath,
        string sourcePath,
        int line,
        ExternalIntegrationKind kind,
        ExternalIntegrationDirection direction,
        string technology,
        string? target,
        string? resourceType = null,
        ExternalIntegrationConfidence confidence = ExternalIntegrationConfidence.High,
        string? contract = null)
    {
        string category = kind switch
        {
            ExternalIntegrationKind.Http => "external.http.outbound",
            ExternalIntegrationKind.Database => "external.database",
            ExternalIntegrationKind.Cache => "external.cache",
            ExternalIntegrationKind.Storage => "external.storage",
            _ => "external.integration.unknown",
        };
        string description = "Bounded external integration evidence.";
        string id = StableIds.ForEvidence(
            category,
            sourcePath,
            line,
            projectPath + "|" + kind + "|" + direction + "|" + technology + "|" + target);
        return new ExternalIntegrationEvidence(
            id,
            category,
            description,
            "integration-0000000000000001",
            projectPath,
            kind,
            direction,
            technology,
            target,
            resourceType,
            null,
            contract,
            sourcePath,
            line,
            confidence,
            ImmutableArray.Create("test:signal"));
    }
}
