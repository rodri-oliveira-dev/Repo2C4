using Repo2C4.Core.Contracts;
using Repo2C4.Core.LikeC4;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class LikeC4EmitterTests
{
    [Theory]
    [InlineData("acme.c1.v1.json", "acme-c1")]
    [InlineData("acme.c2.v1.json", "acme-c2")]
    [InlineData("library-only.c2.partial.v1.json", "library-only-c2")]
    public void CheckedInModelsMatchGoldenLikeC4Files(string modelFile, string goldenFolder)
    {
        ArchitectureModel model = LoadModel(modelFile);
        IReadOnlyList<LikeC4GeneratedFile> generated = LikeC4Emitter.Emit(model);

        Assert.Equal(
            [LikeC4Emitter.SpecificationFileName, LikeC4Emitter.ModelFileName, LikeC4Emitter.ViewsFileName],
            generated.Select(file => file.FileName));

        foreach (LikeC4GeneratedFile file in generated)
        {
            string expected = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "LikeC4Golden", goldenFolder, file.FileName))
                .ReplaceLineEndings("\n");

            Assert.DoesNotContain('\r', file.Content);
            Assert.Equal(expected, file.Content);
        }
    }

    [Fact]
    public void EmissionIsIndependentOfInputCollectionOrder()
    {
        ArchitectureModel model = LoadModel("acme.c2.v1.json");
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

        Assert.Equal(LikeC4Emitter.Emit(model), LikeC4Emitter.Emit(reordered));
    }

    [Fact]
    public void QuotesUnicodeLineBreaksAndDslTokensRemainInsideEscapedStrings()
    {
        ArchitectureModel model = CreateReviewableC1(
            [
                Element("el_one", "API \"café\" ☕\n{ nested }"),
                Element("el_two", "Billing"),
            ],
            [
                Relation(
                    "rel_one_two",
                    "el_one",
                    "el_two",
                    "calls \"billing\"\n} softwareSystem injected = softwareSystem \"Injected\""),
            ]);

        string content = FileContent(LikeC4Emitter.Emit(model), LikeC4Emitter.ModelFileName);

        Assert.Contains("API \\\"café\\\" ☕\n{ nested }", content, StringComparison.Ordinal);
        Assert.Contains(
            "el_one -> el_two \"calls \\\"billing\\\"\n} softwareSystem injected = softwareSystem \\\"Injected\\\"\" {",
            content,
            StringComparison.Ordinal);
        Assert.DoesNotContain('\r', content);
    }

    [Fact]
    public void IdentifiersThatCollideAfterLikeC4NormalizationAreRejected()
    {
        ArchitectureModel model = CreateReviewableC1(
            [
                Element("el.one", "One"),
                Element("el_one", "Two"),
            ],
            []);

        ContractValidationException error = Assert.Throws<ContractValidationException>(() => LikeC4Emitter.Emit(model));

        Assert.Contains(error.Errors, item => item.Code == "likec4.identifierCollision");
    }

    [Fact]
    public void InvalidArchitectureIdentifierIsRejectedBeforeEmission()
    {
        ArchitectureModel model = CreateReviewableC1([Element("Invalid.Id", "One")], []);

        ContractValidationException error = Assert.Throws<ContractValidationException>(() => LikeC4Emitter.Emit(model));

        Assert.Contains(error.Errors, item => item.Code == "id.invalid");
    }

    [Fact]
    public void DuplicateElementsAreRejectedBeforeEmission()
    {
        ArchitectureElement element = Element("el_same", "One");
        ArchitectureModel model = CreateReviewableC1([element, element], []);

        ContractValidationException error = Assert.Throws<ContractValidationException>(() => LikeC4Emitter.Emit(model));

        Assert.Contains(error.Errors, item => item.Code == "id.duplicate");
    }

    [Fact]
    public void MissingRelationEndpointIsRejectedBeforeEmission()
    {
        ArchitectureModel model = CreateReviewableC1(
            [Element("el_one", "One")],
            [Relation("rel_missing", "el_one", "el_missing", "Calls")]);

        ContractValidationException error = Assert.Throws<ContractValidationException>(() => LikeC4Emitter.Emit(model));

        Assert.Contains(error.Errors, item => item.Code == "relation.destinationMissing");
    }

    [Fact]
    public void ElementWithoutRequiredNameIsRejectedBeforeEmission()
    {
        ArchitectureModel model = CreateReviewableC1([Element("el_one", " ")], []);

        ContractValidationException error = Assert.Throws<ContractValidationException>(() => LikeC4Emitter.Emit(model));

        Assert.Contains(error.Errors, item => item.Code == "text.required");
    }

    [Fact]
    public void PartialC2ModelDoesNotInventContainersOrRelations()
    {
        ArchitectureModel model = LoadModel("library-only.c2.partial.v1.json");
        IReadOnlyList<LikeC4GeneratedFile> generated = LikeC4Emitter.Emit(model);
        string modelSource = FileContent(generated, LikeC4Emitter.ModelFileName);
        string viewsSource = FileContent(generated, LikeC4Emitter.ViewsFileName);

        Assert.DoesNotContain(" = container ", modelSource, StringComparison.Ordinal);
        Assert.DoesNotContain(" -> ", modelSource, StringComparison.Ordinal);
        Assert.Contains("include el_library_repo", viewsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("include el_library_repo.", viewsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewStateAndProvenanceRemainVisibleInGeneratedDsl()
    {
        ArchitectureModel model = LoadModel("acme.c2.v1.json");
        string content = FileContent(LikeC4Emitter.Emit(model), LikeC4Emitter.ModelFileName);

        Assert.Contains("#requires-review", content, StringComparison.Ordinal);
        Assert.Contains("reviewStatus \"requiresReview\"", content, StringComparison.Ordinal);
        Assert.Contains("reviewReason \"Package presence does not prove runtime database communication.\"", content, StringComparison.Ordinal);
        Assert.Contains("evidenceIds [\"ev_postgresql_candidate\"]", content, StringComparison.Ordinal);
    }

    private static ArchitectureElement Element(string id, string name) =>
        new(
            id,
            ArchitectureElementKind.SoftwareSystem,
            name,
            null,
            [],
            ReviewStatus.RequiresReview,
            "Proposed for deterministic emitter testing.");

    private static ArchitectureRelation Relation(string id, string sourceId, string destinationId, string description) =>
        new(
            id,
            sourceId,
            destinationId,
            description,
            [],
            ReviewStatus.RequiresReview,
            "Proposed for deterministic emitter testing.");

    private static ArchitectureModel CreateReviewableC1(
        ArchitectureElement[] elements,
        ArchitectureRelation[] relations) =>
        new(
            ContractSchema.Version,
            ArchitectureLevel.C1,
            new RepositorySnapshot(ContractSchema.Version, "emitter_test", [], [], []),
            [.. elements],
            [.. relations]);

    private static ArchitectureModel LoadModel(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "MappingFixtures", fileName);
        return ContractJson.DeserializeModel(File.ReadAllText(path));
    }

    private static string FileContent(IReadOnlyList<LikeC4GeneratedFile> files, string fileName) =>
        Assert.Single(files, file => file.FileName == fileName).Content;
}
