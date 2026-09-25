using System.Collections.Immutable;
using Repo2C4.Core.Contracts;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class ContractTests
{
    [Fact]
    public void SnapshotJsonRoundTripProducesCanonicalJson()
    {
        RepositorySnapshot snapshot = CreateSnapshot();
        string json = ContractJson.SerializeSnapshot(snapshot);
        RepositorySnapshot restored = ContractJson.DeserializeSnapshot(json);

        Assert.Equal(json, ContractJson.SerializeSnapshot(restored));
        Assert.Equal(ContractSchema.Version, restored.SchemaVersion);
        Assert.Equal(2, restored.Files.Length);
        Assert.Equal("src/App/App.csproj", restored.Files[1].RelativePath);
        Assert.DoesNotContain("C:\\", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelJsonRoundTripRetainsC2EvidenceAndReviewState()
    {
        ArchitectureModel model = CreateModel();
        string json = ContractJson.SerializeModel(model);
        ArchitectureModel restored = ContractJson.DeserializeModel(json);

        Assert.Equal(json, ContractJson.SerializeModel(restored));
        Assert.Equal(ArchitectureLevel.C2, restored.Level);
        Assert.Equal(ReviewStatus.RequiresReview, restored.Relations[0].Status);
        Assert.Contains("\"requiresReview\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceType\": \"projectFile\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializationIsIndependentOfCollectionOrder()
    {
        ArchitectureModel model = CreateModel();
        ArchitectureModel reordered = model with
        {
            Snapshot = model.Snapshot with
            {
                Files = [.. model.Snapshot.Files.Reverse()],
                Evidence = [.. model.Snapshot.Evidence.Reverse()],
            },
            Elements = [.. model.Elements.Reverse()],
            Relations = [.. model.Relations.Reverse()],
        };

        Assert.Equal(ContractJson.SerializeModel(model), ContractJson.SerializeModel(reordered));
        Assert.Equal(ContractJson.SerializeSnapshot(model.Snapshot), ContractJson.SerializeSnapshot(reordered.Snapshot));
    }

    [Fact]
    public void StableIdsUseLogicalKeysAndDoNotDependOnEnumerationOrder()
    {
        string id = StableIds.ForEvidence("project", "src/App/App.csproj", 12, "Declares a project.");
        Assert.Equal(id, StableIds.ForEvidence("project", "src/App/App.csproj", 12, "Declares a project."));
        Assert.NotEqual(id, StableIds.ForEvidence("project", "src/App/App.csproj", 13, "Declares a project."));
        Assert.Equal(
            StableIds.ForElement(ArchitectureElementKind.SoftwareSystem, "app"),
            StableIds.ForElement(ArchitectureElementKind.SoftwareSystem, "app"));
        Assert.NotEqual(
            StableIds.ForRelation("el_one", "el_two", "http"),
            StableIds.ForRelation("el_two", "el_one", "http"));
    }

    [Fact]
    public void DuplicateEvidenceIdsAreRejected()
    {
        RepositorySnapshot snapshot = CreateSnapshot();
        snapshot = snapshot with { Evidence = [snapshot.Evidence[0], snapshot.Evidence[0]] };

        AssertHasError(ContractValidator.ValidateSnapshot(snapshot), "id.duplicate");
        Assert.Throws<ContractValidationException>(() => ContractJson.SerializeSnapshot(snapshot));
    }

    [Fact]
    public void DuplicateElementsAndRelationIdsAreRejected()
    {
        ArchitectureModel model = CreateModel();
        AssertHasError(
            ContractValidator.ValidateModel(model with { Elements = [model.Elements[0], model.Elements[0]] }),
            "id.duplicate");

        ArchitectureRelation relation = model.Relations[0];
        AssertHasError(
            ContractValidator.ValidateModel(model with { Relations = [relation, relation] }),
            "id.duplicate");
    }

    [Fact]
    public void MissingRelationDestinationIsRejected()
    {
        ArchitectureModel model = CreateModel();
        ArchitectureRelation relation = model.Relations[0] with { DestinationId = "el_missing" };
        AssertHasError(ContractValidator.ValidateModel(model with { Relations = [relation] }), "relation.destinationMissing");
    }

    [Fact]
    public void MissingEvidenceReferenceIsRejected()
    {
        ArchitectureModel model = CreateModel();
        ArchitectureElement element = model.Elements[0] with { EvidenceIds = ["ev_unknown"] };
        AssertHasError(
            ContractValidator.ValidateModel(model with { Elements = [element, .. model.Elements.Skip(1)] }),
            "evidence.referenceMissing");
    }

    [Fact]
    public void UnsupportedConfirmedHypothesisMustBeMarkedForReview()
    {
        ArchitectureModel model = CreateModel();
        ArchitectureRelation unsupported = model.Relations[0] with
        {
            Status = ReviewStatus.Confirmed,
            ReviewReason = null,
        };

        AssertHasError(ContractValidator.ValidateModel(model with { Relations = [unsupported] }), "review.unsubstantiated");
        Assert.Throws<ContractValidationException>(() => ContractJson.SerializeModel(model with { Relations = [unsupported] }));
    }

    [Fact]
    public void InferenceRequiresAnExplicitReviewReason()
    {
        ArchitectureModel model = CreateModel();
        ArchitectureRelation relation = model.Relations[0] with { ReviewReason = null };
        AssertHasError(ContractValidator.ValidateModel(model with { Relations = [relation] }), "review.reason");
    }

    [Fact]
    public void CyclicContainmentIsRejected()
    {
        ArchitectureModel model = CreateModel();
        ArchitectureElement system = model.Elements[0] with { ParentId = "el_container" };
        ArchitectureElement container = model.Elements[2] with { ParentId = "el_system" };
        AssertHasError(
            ContractValidator.ValidateModel(model with { Elements = [system, model.Elements[1], container] }),
            "containment.cycle");
    }

    [Fact]
    public void ContainerCannotAppearInC1()
    {
        ArchitectureModel model = CreateModel();
        AssertHasError(ContractValidator.ValidateModel(model with { Level = ArchitectureLevel.C1 }), "level.container");
    }

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\users\\secret")]
    [InlineData("a//b")]
    [InlineData("src/./File.cs")]
    [InlineData("src/../File.cs")]
    public void PathsMustBeNormalizedAndConfinedToRepository(string path)
    {
        Assert.False(ContractValidator.IsNormalizedRelativePath(path));
        Assert.Throws<ArgumentException>(() => StableIds.ForEvidence("file", path, 1, "Test"));
    }

    [Fact]
    public void EvidenceMustReferToInventoriedFile()
    {
        RepositorySnapshot snapshot = CreateSnapshot();
        Evidence evidence = snapshot.Evidence[0] with { RelativePath = "not-in-snapshot.cs" };
        AssertHasError(
            ContractValidator.ValidateSnapshot(snapshot with { Evidence = [evidence, snapshot.Evidence[1]] }),
            "evidence.fileMissing");
    }

    [Fact]
    public void IncompatibleSchemaIsRejected()
    {
        RepositorySnapshot snapshot = CreateSnapshot() with { SchemaVersion = "2.0" };
        AssertHasError(ContractValidator.ValidateSnapshot(snapshot), "schema.unsupported");
        Assert.Throws<ContractValidationException>(() => ContractJson.SerializeSnapshot(snapshot));
    }

    [Fact]
    public void DefaultCollectionsAreReportedInsteadOfThrowing()
    {
        RepositorySnapshot snapshot = CreateSnapshot() with { Evidence = default };
        AssertHasError(ContractValidator.ValidateSnapshot(snapshot), "collection.missing");

        ArchitectureModel model = CreateModel() with { Elements = default };
        AssertHasError(ContractValidator.ValidateModel(model), "collection.missing");
    }

    [Fact]
    public void InvalidJsonYieldsControlledDiagnostic()
    {
        ContractValidationException error = Assert.Throws<ContractValidationException>(
            () => ContractJson.DeserializeModel("{"));
        AssertHasError(error.Errors, "json.invalid");
    }

    [Fact]
    public void SnapshotSupportsNoEvidenceForAnInitialBoundedInventory()
    {
        RepositorySnapshot snapshot = new(ContractSchema.Version, "repo2c4", [], [], []);
        Assert.Empty(ContractValidator.ValidateSnapshot(snapshot));
        Assert.Equal(snapshot, ContractJson.DeserializeSnapshot(ContractJson.SerializeSnapshot(snapshot)));
    }

    private static RepositorySnapshot CreateSnapshot() =>
        new(
            ContractSchema.Version,
            "repo2c4",
            [
                new RepositoryFile("src/App/App.csproj", 120, null),
                new RepositoryFile("Repo2C4.slnx", 48, null),
            ],
            [
                new Evidence("ev_system", "solution", "Repo2C4.slnx", 1, EvidenceSourceType.Manifest, "Solution declares App."),
                new Evidence("ev_container", "project", "src/App/App.csproj", 1, EvidenceSourceType.ProjectFile, "App is a .NET project."),
            ],
            []);

    private static ArchitectureModel CreateModel()
    {
        RepositorySnapshot snapshot = CreateSnapshot();
        return new ArchitectureModel(
            ContractSchema.Version,
            ArchitectureLevel.C2,
            snapshot,
            [
                new ArchitectureElement(
                    "el_system", ArchitectureElementKind.SoftwareSystem, "App system", null,
                    ["ev_system"], ReviewStatus.Confirmed, null),
                new ArchitectureElement(
                    "el_actor", ArchitectureElementKind.Actor, "User", null,
                    [], ReviewStatus.RequiresReview, "Actor was proposed for review, not discovered."),
                new ArchitectureElement(
                    "el_container", ArchitectureElementKind.Container, "App container", "el_system",
                    ["ev_container"], ReviewStatus.Confirmed, null),
            ],
            [
                new ArchitectureRelation(
                    "rel_user_app", "el_actor", "el_container", "Uses",
                    [], ReviewStatus.RequiresReview, "No observable use relation was discovered."),
            ]);
    }

    private static void AssertHasError(ImmutableArray<ContractError> errors, string code)
    {
        Assert.True(errors.Any(error => error.Code == code), "Expected error code: " + code);
    }
}
