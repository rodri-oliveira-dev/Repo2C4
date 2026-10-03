using Repo2C4.Core.C3;
using Repo2C4.Core.Contracts;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class SemanticC3BaselineTests
{
    [Fact]
    public void V110DogfoodEquivalentCapturesGenericC3Baseline()
    {
        ArchitectureModel c2 = LoadModel("dotnet-observability-lab.ingestion.v1.1.c2.json");

        ArchitectureC3Model c3 = ArchitectureC3Builder.Build(c2, "el_ingestion_api");
        ArchitectureC3Model repeated = ArchitectureC3Builder.Build(c2, "el_ingestion_api");

        Assert.Equal(
            ["Application dependency", "HTTP interface", "Integration adapter"],
            c3.Components
                .Select(component => component.Name)
                .OrderBy(name => name, StringComparer.Ordinal));

        Assert.All(c3.Components, component => Assert.Equal(ReviewStatus.RequiresReview, component.Status));
        Assert.Contains(c3.Relations, relation => relation.DestinationId == "el_postgresql");
        Assert.Contains(c3.Relations, relation => relation.DestinationId == "el_redis");
        Assert.Equal(
            c3.Components.Select(component => component.Id),
            repeated.Components.Select(component => component.Id));
        Assert.Equal(
            c3.Relations.Select(relation => relation.Id),
            repeated.Relations.Select(relation => relation.Id));

        Assert.DoesNotContain(
            c3.Components,
            component => component.Name == "HTTP ingestion endpoint");
        Assert.DoesNotContain(
            c3.Components,
            component => component.Name == "Idempotent ingestion use case");
        Assert.DoesNotContain(
            c3.Components,
            component => component.Name == "Transactional persistence");
    }

    private static ArchitectureModel LoadModel(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "SemanticC3Fixtures", fileName);
        return ContractJson.DeserializeModel(File.ReadAllText(path));
    }
}
