using Repo2C4.Core.Contracts;
using Xunit;

namespace Repo2C4.Mcp.Tests;

public sealed class McpLikeC4ToolsTests
{
    [Fact]
    public async Task DryRunPreviewsFilesWithoutWriting()
    {
        using TempDirectory temp = new();
        using McpSnapshotStore store = new();
        RepositorySnapshot snapshot = CreateSnapshot();
        McpSnapshotStore.SnapshotEntry entry = store.Store(snapshot);
        McpLikeC4Tools tools = new(temp.Path, store);

        McpGenerateLikeC4Result result = await tools.GenerateLikeC4(
            entry.SnapshotId,
            CreateModel(snapshot),
            destinationPath: "architecture",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.DryRun);
        Assert.False(result.Written);
        Assert.Equal(3, result.Files.Length);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "architecture")));
    }

    [Fact]
    public async Task WriteRequiresExplicitAuthorizationAndNeverOverwrites()
    {
        using TempDirectory temp = new();
        using McpSnapshotStore store = new();
        RepositorySnapshot snapshot = CreateSnapshot();
        McpSnapshotStore.SnapshotEntry entry = store.Store(snapshot);
        McpLikeC4Tools tools = new(temp.Path, store);
        ArchitectureModel model = CreateModel(snapshot);

        Exception disabled = await Assert.ThrowsAnyAsync<Exception>(
            () => tools.GenerateLikeC4(
                entry.SnapshotId,
                model,
                dryRun: false,
                write: false,
                destinationPath: "architecture",
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("write_not_authorized", disabled.Message, StringComparison.Ordinal);

        McpGenerateLikeC4Result written = await tools.GenerateLikeC4(
            entry.SnapshotId,
            model,
            dryRun: false,
            write: true,
            destinationPath: "architecture",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(written.Written);
        Assert.All(
            written.Files,
            file => Assert.True(File.Exists(Path.Combine(temp.Path, "architecture", file.FileName))));

        Exception conflict = await Assert.ThrowsAnyAsync<Exception>(
            () => tools.GenerateLikeC4(
                entry.SnapshotId,
                model,
                dryRun: false,
                write: true,
                destinationPath: "architecture",
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("destination_exists", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteCreatesNestedDestinationWithinAuthorizedRoot()
    {
        using TempDirectory temp = new();
        using McpSnapshotStore store = new();
        RepositorySnapshot snapshot = CreateSnapshot();
        McpSnapshotStore.SnapshotEntry entry = store.Store(snapshot);
        McpLikeC4Tools tools = new(temp.Path, store);

        McpGenerateLikeC4Result result = await tools.GenerateLikeC4(
            entry.SnapshotId,
            CreateModel(snapshot),
            dryRun: false,
            write: true,
            destinationPath: "artifacts/architecture/c1",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Written);
        Assert.True(File.Exists(Path.Combine(
            temp.Path,
            "artifacts",
            "architecture",
            "c1",
            "model.c4")));
    }

    [Fact]
    public async Task WriteRejectsTraversalAndMissingSnapshot()
    {
        using TempDirectory temp = new();
        using McpSnapshotStore store = new();
        RepositorySnapshot snapshot = CreateSnapshot();
        McpSnapshotStore.SnapshotEntry entry = store.Store(snapshot);
        McpLikeC4Tools tools = new(temp.Path, store);
        ArchitectureModel model = CreateModel(snapshot);

        Exception traversal = await Assert.ThrowsAnyAsync<Exception>(
            () => tools.GenerateLikeC4(
                entry.SnapshotId,
                model,
                dryRun: false,
                write: true,
                destinationPath: "../outside",
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("destination_invalid", traversal.Message, StringComparison.Ordinal);

        Exception missing = await Assert.ThrowsAnyAsync<Exception>(
            () => tools.GenerateLikeC4(
                "snapshot_missing",
                model,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("snapshot_not_found_or_expired", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelSchemaAndFabricatedEvidenceAreRejected()
    {
        using TempDirectory temp = new();
        using McpSnapshotStore store = new();
        RepositorySnapshot snapshot = CreateSnapshot();
        McpSnapshotStore.SnapshotEntry entry = store.Store(snapshot);
        McpLikeC4Tools tools = new(temp.Path, store);

        ArchitectureModel invalidSchema = CreateModel(snapshot) with
        {
            SchemaVersion = "9.0",
        };
        Exception schema = await Assert.ThrowsAnyAsync<Exception>(
            () => tools.GenerateLikeC4(
                entry.SnapshotId,
                invalidSchema,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("model_invalid", schema.Message, StringComparison.Ordinal);
        Assert.Contains("schema.unsupported", schema.Message, StringComparison.Ordinal);

        Evidence fabricated = new(
            "ev_fabricated",
            "documentation.runtime.confirmed",
            "App.csproj",
            1,
            EvidenceSourceType.Documentation,
            "Fabricated evidence.");
        RepositorySnapshot fabricatedSnapshot = snapshot with
        {
            Evidence = [.. snapshot.Evidence, fabricated],
        };
        ArchitectureModel fabricatedModel = CreateModel(fabricatedSnapshot);

        Exception mismatch = await Assert.ThrowsAnyAsync<Exception>(
            () => tools.GenerateLikeC4(
                entry.SnapshotId,
                fabricatedModel,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("snapshot_mismatch", mismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StaticCandidateEvidenceCannotBecomeConfirmedContainerOrRuntimeRelation()
    {
        using TempDirectory temp = new();
        using McpSnapshotStore store = new();
        RepositorySnapshot snapshot = CreateSnapshot("dotnet.runtime.http.candidate");
        McpSnapshotStore.SnapshotEntry entry = store.Store(snapshot);
        McpLikeC4Tools tools = new(temp.Path, store);

        ArchitectureElement system = new(
            "el_system",
            ArchitectureElementKind.SoftwareSystem,
            "System",
            null,
            ["ev_observed"],
            ReviewStatus.RequiresReview,
            "System boundary requires review.");
        ArchitectureElement container = new(
            "el_web",
            ArchitectureElementKind.Container,
            "Web",
            "el_system",
            ["ev_observed"],
            ReviewStatus.Confirmed,
            null);
        ArchitectureModel containerModel = new(
            ContractSchema.Version,
            ArchitectureLevel.C2,
            snapshot,
            [system, container],
            []);

        Exception containerError = await Assert.ThrowsAnyAsync<Exception>(
            () => tools.GenerateLikeC4(
                entry.SnapshotId,
                containerModel,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("model_review_required", containerError.Message, StringComparison.Ordinal);

        ArchitectureElement actor = new(
            "el_actor",
            ArchitectureElementKind.Actor,
            "Actor",
            null,
            ["ev_observed"],
            ReviewStatus.RequiresReview,
            "Actor requires review.");
        ArchitectureRelation relation = new(
            "rel_runtime",
            "el_actor",
            "el_system",
            "Uses",
            ["ev_observed"],
            ReviewStatus.Confirmed,
            null);
        ArchitectureModel relationModel = new(
            ContractSchema.Version,
            ArchitectureLevel.C1,
            snapshot,
            [actor, system],
            [relation]);

        Exception relationError = await Assert.ThrowsAnyAsync<Exception>(
            () => tools.GenerateLikeC4(
                entry.SnapshotId,
                relationModel,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("model_review_required", relationError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationStopsGenerationBeforeFilesystemChanges()
    {
        using TempDirectory temp = new();
        using McpSnapshotStore store = new();
        RepositorySnapshot snapshot = CreateSnapshot();
        McpSnapshotStore.SnapshotEntry entry = store.Store(snapshot);
        McpLikeC4Tools tools = new(temp.Path, store);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tools.GenerateLikeC4(
                entry.SnapshotId,
                CreateModel(snapshot),
                dryRun: false,
                write: true,
                destinationPath: "architecture",
                cancellationToken: cancellation.Token));

        Assert.False(Directory.Exists(Path.Combine(temp.Path, "architecture")));
    }

    private static RepositorySnapshot CreateSnapshot(string category = "dotnet.project.kind")
    {
        RepositoryFile file = new("App.csproj", 128, null);
        Evidence evidence = new(
            "ev_observed",
            category,
            file.RelativePath,
            1,
            EvidenceSourceType.ProjectFile,
            "Observed static repository declaration.");
        return new RepositorySnapshot(
            ContractSchema.Version,
            "repo_test",
            [file],
            [evidence],
            []);
    }

    private static ArchitectureModel CreateModel(RepositorySnapshot snapshot)
    {
        ArchitectureElement system = new(
            "el_system",
            ArchitectureElementKind.SoftwareSystem,
            "System",
            null,
            [snapshot.Evidence[0].Id],
            ReviewStatus.RequiresReview,
            "Repository evidence alone does not confirm the deployment boundary.");
        return new ArchitectureModel(
            ContractSchema.Version,
            ArchitectureLevel.C1,
            snapshot,
            [system],
            []);
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = Directory.CreateTempSubdirectory("repo2c4-mcp-likec4-test-").FullName;
        }

        internal string Path
        {
            get;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
