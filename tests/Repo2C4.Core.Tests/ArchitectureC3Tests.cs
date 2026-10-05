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
    public void ComponentIdCollidingWithBaseElementIsRejectedBeforeEmission()
    {
        ArchitectureModel c2 = LoadModel("acme.c2.v1.json");
        ArchitectureC3Model c3 = ArchitectureC3Builder.Build(c2, "el_web");
        Assert.NotEmpty(c3.Components);
        ArchitectureC3Model colliding = c3 with
        {
            Components =
            [
                c3.Components[0] with
                {
                    Id = "el_web",
                },
                .. c3.Components.Skip(1),
            ],
        };

        Assert.Contains(
            ArchitectureC3Validator.Validate(colliding),
            error => error.Code == "id.duplicate" && error.Path == "$.components[0].id");

        ContractValidationException exception = Assert.Throws<ContractValidationException>(
            () => LikeC4Emitter.EmitWithC3(c2, colliding));
        Assert.Contains(
            exception.Errors,
            error => error.Code == "id.duplicate" && error.Path == "$.components[0].id");
    }

    [Fact]
    public void MalformedBaseAndRelationIdsReturnValidationErrors()
    {
        ArchitectureModel c2 = LoadModel("acme.c2.v1.json");
        ArchitectureC3Model valid = ArchitectureC3Builder.Build(c2, "el_web");
        ArchitectureC3Model invalidBase = valid with
        {
            BaseModel = c2 with
            {
                Elements = default,
            },
        };

        Assert.Contains(
            ArchitectureC3Validator.Validate(invalidBase),
            error => error.Path.StartsWith("$.baseModel", StringComparison.Ordinal));

        ArchitectureC3Model missingContainer = valid with
        {
            SelectedContainerId = null!,
        };
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
        ArchitectureC3Model invalidIds = valid with
        {
            Relations = [invalidRelation],
        };
        IReadOnlyList<ContractError> errors = ArchitectureC3Validator.Validate(invalidIds);
        Assert.Contains(errors, error => error.Code == "relation.sourceMissing");
        Assert.Contains(errors, error => error.Code == "relation.destinationMissing");
    }

    [Fact]
    public void ZeroSelectionsKeepC2OnlyOutput()
    {
        ArchitectureModel c2 = LoadModel("multi-c3.c2.v1.json");

        ArchitectureC3Workspace workspace = ArchitectureC3Builder.BuildMany(c2, []);
        IReadOnlyList<LikeC4GeneratedFile> actual = LikeC4Emitter.EmitWithC3(workspace);
        IReadOnlyList<LikeC4GeneratedFile> expected = LikeC4Emitter.Emit(c2);

        Assert.Empty(workspace.Selections);
        Assert.Equal(
            expected.Select(file => (file.FileName, file.Content)),
            actual.Select(file => (file.FileName, file.Content)));
        Assert.DoesNotContain(actual, file => file.FileName == LikeC4Emitter.C3ViewsFileName);
    }

    [Fact]
    public void SingleSelectionWorkspaceIsBackwardCompatibleWithLegacyEmission()
    {
        ArchitectureModel c2 = LoadModel("multi-c3.c2.v1.json");
        ArchitectureC3Model single = ArchitectureC3Builder.Build(c2, "el_alpha");
        ArchitectureC3Workspace workspace = ArchitectureC3Builder.BuildMany(c2, ["el_alpha"]);

        IReadOnlyList<LikeC4GeneratedFile> legacy = LikeC4Emitter.EmitWithC3(c2, single);
        IReadOnlyList<LikeC4GeneratedFile> multi = LikeC4Emitter.EmitWithC3(workspace);

        Assert.Equal(
            legacy.Select(file => (file.FileName, file.Content)),
            multi.Select(file => (file.FileName, file.Content)));
        Assert.Contains(
            "view c3 {",
            multi.Single(file => file.FileName == LikeC4Emitter.C3ViewsFileName).Content,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleSelectionsAreCanonicalDeduplicatedAndShareOneWorkspace()
    {
        ArchitectureModel c2 = LoadModel("multi-c3.c2.v1.json");

        ArchitectureC3Workspace workspace = ArchitectureC3Builder.BuildMany(
            c2,
            ["el_delta", "el_alpha", "el_beta", "el_alpha", "el_gamma"]);
        IReadOnlyList<LikeC4GeneratedFile> files = LikeC4Emitter.EmitWithC3(workspace);

        Assert.Equal(
            ["el_alpha", "el_beta", "el_delta", "el_gamma"],
            workspace.Selections.Select(selection => selection.SelectedContainerId));

        string model = files.Single(file => file.FileName == LikeC4Emitter.ModelFileName).Content;
        Assert.Equal(4, model.Split(" = component ", StringSplitOptions.None).Length - 1);

        string views = files.Single(file => file.FileName == LikeC4Emitter.C3ViewsFileName).Content;
        Assert.Contains("view c3_el_alpha {", views, StringComparison.Ordinal);
        Assert.Contains("view c3_el_beta {", views, StringComparison.Ordinal);
        Assert.Contains("view c3_el_delta {", views, StringComparison.Ordinal);
        Assert.Contains("view c3_el_gamma {", views, StringComparison.Ordinal);
        Assert.Contains("C3 - Alpha API", views, StringComparison.Ordinal);
        Assert.Contains("C3 - Beta API", views, StringComparison.Ordinal);
        Assert.Contains("C3 - Gamma API", views, StringComparison.Ordinal);
        Assert.Contains("C3 - Delta API", views, StringComparison.Ordinal);
        Assert.Equal(1, views.Split("views {", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void SelectionArgumentOrderDoesNotChangeGeneratedWorkspace()
    {
        ArchitectureModel c2 = LoadModel("multi-c3.c2.v1.json");

        ArchitectureC3Workspace first = ArchitectureC3Builder.BuildMany(
            c2,
            ["el_gamma", "el_alpha", "el_beta"]);
        ArchitectureC3Workspace second = ArchitectureC3Builder.BuildMany(
            c2,
            ["el_beta", "el_gamma", "el_alpha", "el_alpha"]);

        IReadOnlyList<LikeC4GeneratedFile> firstFiles = LikeC4Emitter.EmitWithC3(first);
        IReadOnlyList<LikeC4GeneratedFile> secondFiles = LikeC4Emitter.EmitWithC3(second);

        Assert.Equal(
            firstFiles.Select(file => (file.FileName, file.Content)),
            secondFiles.Select(file => (file.FileName, file.Content)));
    }

    [Fact]
    public void InvalidOrNonContainerSelectionsAndC1BaseAreRejected()
    {
        ArchitectureModel c2 = LoadModel("multi-c3.c2.v1.json");

        ContractValidationException missing = Assert.Throws<ContractValidationException>(
            () => ArchitectureC3Builder.BuildMany(c2, ["el_missing"]));
        Assert.Contains(missing.Errors, error => error.Code == "c3.containerMissing");

        ContractValidationException system = Assert.Throws<ContractValidationException>(
            () => ArchitectureC3Builder.BuildMany(c2, ["el_suite"]));
        Assert.Contains(system.Errors, error => error.Code == "c3.containerKind");

        ContractValidationException actor = Assert.Throws<ContractValidationException>(
            () => ArchitectureC3Builder.BuildMany(c2, ["el_user"]));
        Assert.Contains(actor.Errors, error => error.Code == "c3.containerKind");

        ArchitectureModel c1 = c2 with
        {
            Level = ArchitectureLevel.C1,
            Elements =
            [
                .. c2.Elements.Where(element =>
                    element.Kind is ArchitectureElementKind.Actor or ArchitectureElementKind.SoftwareSystem),
            ],
            Relations = [],
        };

        ContractValidationException invalidLevel = Assert.Throws<ContractValidationException>(
            () => ArchitectureC3Builder.BuildMany(c1, ["el_suite"]));
        Assert.Contains(invalidLevel.Errors, error => error.Code == "c3.baseLevel");
    }

    private static ArchitectureModel LoadModel(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MappingFixtures", fileName);
        return ContractJson.DeserializeModel(File.ReadAllText(path));
    }
}
