using Repo2C4.Core.Contracts;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class C4MappingFixtureTests
{
    [Theory]
    [InlineData("acme.c1.v1.json", ArchitectureLevel.C1)]
    [InlineData("acme.c2.v1.json", ArchitectureLevel.C2)]
    [InlineData("library-only.c2.partial.v1.json", ArchitectureLevel.C2)]
    public void CheckedInMappingFixturesAreValidV1Models(string fileName, ArchitectureLevel expectedLevel)
    {
        ArchitectureModel model = LoadModel(fileName);

        Assert.Equal(expectedLevel, model.Level);
        Assert.Empty(ContractValidator.ValidateModel(model));

        string canonical = ContractJson.SerializeModel(model);
        ArchitectureModel roundTrip = ContractJson.DeserializeModel(canonical);

        Assert.Equal(canonical, ContractJson.SerializeModel(roundTrip));
    }

    [Fact]
    public void C1FixtureDoesNotInventActorOrProtocol()
    {
        ArchitectureModel model = LoadModel("acme.c1.v1.json");

        Assert.DoesNotContain(model.Elements, element => element.Kind == ArchitectureElementKind.Actor);
        Assert.All(model.Relations, relation => Assert.Equal(ReviewStatus.RequiresReview, relation.Status));
        Assert.DoesNotContain(
            model.Relations,
            relation => relation.Description.Contains("http", StringComparison.OrdinalIgnoreCase)
                || relation.Description.Contains("amqp", StringComparison.OrdinalIgnoreCase)
                || relation.Description.Contains("grpc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void C2FixtureKeepsExecutableAndInfrastructureSignalsUnderReviewAndDoesNotPromoteSharedLibrary()
    {
        ArchitectureModel model = LoadModel("acme.c2.v1.json");
        Dictionary<string, Evidence> evidenceById = model.Snapshot.Evidence.ToDictionary(item => item.Id, StringComparer.Ordinal);

        Assert.DoesNotContain(
            model.Elements,
            element => element.Kind == ArchitectureElementKind.Container
                && element.Name.Contains("Shared", StringComparison.OrdinalIgnoreCase));

        ArchitectureElement web = Assert.Single(model.Elements, element => element.Id == "el_web");
        ArchitectureElement worker = Assert.Single(model.Elements, element => element.Id == "el_worker");
        Assert.Equal(ReviewStatus.RequiresReview, web.Status);
        Assert.Equal(ReviewStatus.RequiresReview, worker.Status);

        Assert.Contains(web.EvidenceIds, id => evidenceById[id].Category == "dotnet.runtime.http.candidate");
        Assert.Contains(worker.EvidenceIds, id => evidenceById[id].Category == "dotnet.runtime.worker.candidate");

        Assert.All(
            model.Relations.Where(relation => relation.EvidenceIds.Length > 0),
            relation =>
            {
                bool onlyCandidateEvidence = relation.EvidenceIds
                    .Select(id => evidenceById[id].Category)
                    .All(category => category.EndsWith(".candidate", StringComparison.Ordinal)
                        || category == "dotnet.project.reference");

                if (onlyCandidateEvidence)
                {
                    Assert.Equal(ReviewStatus.RequiresReview, relation.Status);
                }
            });

        Assert.DoesNotContain(
            model.Relations.SelectMany(relation => relation.EvidenceIds),
            id => evidenceById[id].Category == "dotnet.project.reference");

        ArchitectureRelation workerRabbitMq =
            Assert.Single(model.Relations, relation => relation.Id == "rel_worker_rabbitmq");
        Assert.Empty(workerRabbitMq.EvidenceIds);
        string workerReviewReason = Assert.IsType<string>(workerRabbitMq.ReviewReason);
        Assert.Contains("No repository evidence links Worker to RabbitMQ", workerReviewReason, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryOnlyFixtureIsPartialWithoutFabricatedContainersActorsOrRelations()
    {
        ArchitectureModel model = LoadModel("library-only.c2.partial.v1.json");

        Assert.DoesNotContain(model.Elements, element => element.Kind == ArchitectureElementKind.Container);
        Assert.DoesNotContain(model.Elements, element => element.Kind == ArchitectureElementKind.Actor);
        Assert.Empty(model.Relations);

        ArchitectureElement system = Assert.Single(model.Elements);
        Assert.Equal(ReviewStatus.RequiresReview, system.Status);
        string reviewReason = Assert.IsType<string>(system.ReviewReason);
        Assert.Contains("No executable or host candidate", reviewReason, StringComparison.Ordinal);
    }

    private static ArchitectureModel LoadModel(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MappingFixtures", fileName);
        return ContractJson.DeserializeModel(File.ReadAllText(path));
    }
}
