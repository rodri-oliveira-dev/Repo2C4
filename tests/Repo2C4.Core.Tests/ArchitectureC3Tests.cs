using Repo2C4.Core.C3;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.LikeC4;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class ArchitectureC3Tests
{
    [Fact]
    public void SelectedContainerProducesOnlyItsC3Components()
    {
        ArchitectureModel c2 = LoadModel("acme.c2.v1.json");

        ArchitectureC3Model c3 = ArchitectureC3Builder.Build(c2, "el_web");

        Assert.Equal("el_web", c3.SelectedContainerId);
        Assert.NotEmpty(c3.Components);
        Assert.All(c3.Components, item => Assert.Equal("el_web", item.ContainerId));
        Assert.DoesNotContain(c3.Components, item => item.Name.Contains("Worker", StringComparison.OrdinalIgnoreCase));

        IReadOnlyList<LikeC4GeneratedFile> files = LikeC4Emitter.EmitWithC3(c2, c3);
        Assert.Contains(files, file => file.FileName == "c3.views.c4");
        Assert.Contains("component", files.Single(file => file.FileName == "model.c4").Content, StringComparison.Ordinal);
        Assert.DoesNotContain(
            files.Single(file => file.FileName == "c3.views.c4").Content,
            "el_worker",
            StringComparison.Ordinal);

        ArchitectureC3Model repeated = ArchitectureC3Builder.Build(c2, "el_web");
        Assert.Equal(
            c3.Components.Select(item => item.Id),
            repeated.Components.Select(item => item.Id));
        Assert.DoesNotContain(
            c3.Relations,
            relation => relation.Description.Contains("billing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingContainerIsRejected()
    {
        ArchitectureModel c2 = LoadModel("acme.c2.v1.json");

        ContractValidationException error = Assert.Throws<ContractValidationException>(
            () => ArchitectureC3Builder.Build(c2, "el_missing"));

        Assert.Contains(error.Errors, item => item.Code == "c3.containerMissing");
    }

    [Fact]
    public void DependencyWithoutEvidenceOriginIsRejected()
    {
        ArchitectureModel c2 = LoadModel("acme.c2.v1.json");
        ArchitectureC3Model c3 = ArchitectureC3Builder.Build(c2, "el_web");
        ArchitectureComponent first = c3.Components[0] with
        {
            EvidenceIds = ["ev_missing"],
        };
        ArchitectureC3Model invalid = c3 with
        {
            Components =
            [
                first,
                .. c3.Components.Skip(1),
            ],
        };

        Assert.Contains(
            ArchitectureC3Validator.Validate(invalid),
            item => item.Code == "evidence.referenceMissing");
    }

    [Fact]
    public void ContainerWithoutEvidenceProducesInsufficientEvidenceDiagnostic()
    {
        ArchitectureModel c2 = LoadModel("acme.c2.v1.json");
        ArchitectureElement worker = c2.Elements.Single(item => item.Id == "el_worker");
        ArchitectureElement emptyWorker = worker with
        {
            EvidenceIds = [],
            Status = ReviewStatus.RequiresReview,
            ReviewReason = "No component evidence.",
        };
        c2 = c2 with
        {
            Elements = [.. c2.Elements.Select(item => item.Id == worker.Id ? emptyWorker : item)],
        };

        ArchitectureC3Model c3 = ArchitectureC3Builder.Build(c2, "el_worker");
        ContractValidationException error = Assert.Throws<ContractValidationException>(
            () => LikeC4Emitter.EmitWithC3(c2, c3));

        Assert.Contains(error.Errors, item => item.Code == "c3.insufficientEvidence");
    }

    [Fact]
    public void InvalidSchemaAndLargeInputsAreRejected()
    {
        ArchitectureModel c2 = LoadModel("acme.c2.v1.json");
        ArchitectureC3Model c3 = ArchitectureC3Builder.Build(c2, "el_web");

        ArchitectureC3Model invalidSchema = c3 with
        {
            SchemaVersion = "9.0",
        };
        Assert.Contains(
            ArchitectureC3Validator.Validate(invalidSchema),
            item => item.Code == "schema.unsupported");

        ArchitectureComponent seed = c3.Components[0];
        ArchitectureC3Model tooLarge = c3 with
        {
            Components =
            [
                .. Enumerable.Range(0, ArchitectureC3Validator.MaxComponents + 1)
                    .Select(index => seed with
                    {
                        Id = "cmp_" + index,
                    }),
            ],
        };

        Assert.Contains(
            ArchitectureC3Validator.Validate(tooLarge),
            item => item.Code == "c3.componentLimit");
    }

    [Fact]
    public void MalformedBaseAndRelationIdsReturnValidationErrors()
    {
        ArchitectureModel c2 = LoadModel("acme.c2.v1.json");
        ArchitectureC3Model valid = ArchitectureC3Builder.Build(c2, "el_web");
        ArchitectureC3Model invalidBase = valid with
        {
            BaseModel = c2 with { Elements = default },
        };

        Assert.Contains(
            ArchitectureC3Validator.Validate(invalidBase),
            error => error.Path.StartsWith("$.baseModel", StringComparison.Ordinal));

        ArchitectureC3Model missingContainer = valid with { SelectedContainerId = null! };
        Assert.Contains(
            ArchitectureC3Validator.Validate(missingContainer),
            error => error.Code == "c3.containerMissing");

        ArchitectureComponentRelation invalidRelation = new(
            "rel_invalid",
            null!,
            null!,
            "Invalid endpoints",
            [],
            ReviewStatus.RequiresReview,
            "Missing endpoint IDs.");
        ArchitectureC3Model invalidIds = valid with { Relations = [invalidRelation] };
        IReadOnlyList<ContractError> errors = ArchitectureC3Validator.Validate(invalidIds);
        Assert.Contains(errors, error => error.Code == "relation.sourceMissing");
        Assert.Contains(errors, error => error.Code == "relation.destinationMissing");
    }

    private static ArchitectureModel LoadModel(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MappingFixtures", fileName);
        return ContractJson.DeserializeModel(File.ReadAllText(path));
    }
}
