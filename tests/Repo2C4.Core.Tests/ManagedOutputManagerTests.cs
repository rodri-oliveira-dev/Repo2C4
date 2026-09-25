using Repo2C4.Core.Generation;
using Repo2C4.Core.LikeC4;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class ManagedOutputManagerTests
{
    [Fact]
    public async Task InitialPreviewDoesNotWriteAndApplyCreatesManifest()
    {
        using TempDirectory temp = new();
        string output = Path.Combine(temp.Path, "out");
        LikeC4GeneratedFile[] files = [new("model.c4", "model { }\n")];

        GenerationPlan preview = await ManagedOutputManager.PreviewAsync(
            output,
            "1.0",
            files,
            TestContext.Current.CancellationToken);

        Assert.Single(preview.Changes);
        Assert.Equal(GeneratedFileChangeKind.Added, preview.Changes[0].Kind);
        Assert.False(Directory.Exists(output));

        await ManagedOutputManager.CommitAsync(
            output,
            "1.0",
            files,
            preview,
            TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(output, "model.c4")));
        Assert.True(File.Exists(Path.Combine(output, ManagedOutputManager.ManifestFileName)));
    }

    [Fact]
    public async Task UnchangedManagedFileIsIdempotent()
    {
        using TempDirectory temp = new();
        string output = Path.Combine(temp.Path, "out");
        LikeC4GeneratedFile[] files = [new("model.c4", "model { }\n")];
        GenerationPlan first = await ManagedOutputManager.PreviewAsync(output, "1.0", files, TestContext.Current.CancellationToken);
        await ManagedOutputManager.CommitAsync(output, "1.0", files, first, TestContext.Current.CancellationToken);

        GenerationPlan second = await ManagedOutputManager.PreviewAsync(output, "1.0", files, TestContext.Current.CancellationToken);

        Assert.False(second.HasChanges);
        Assert.False(second.HasConflicts);
        Assert.Equal(GeneratedFileChangeKind.Unchanged, Assert.Single(second.Changes).Kind);
    }

    [Fact]
    public async Task ManualEditAndUnmanagedFileAreConflictsAndRemainUntouched()
    {
        using TempDirectory temp = new();
        string output = Path.Combine(temp.Path, "out");
        LikeC4GeneratedFile[] files = [new("model.c4", "model { }\n")];
        GenerationPlan first = await ManagedOutputManager.PreviewAsync(output, "1.0", files, TestContext.Current.CancellationToken);
        await ManagedOutputManager.CommitAsync(output, "1.0", files, first, TestContext.Current.CancellationToken);

        string target = Path.Combine(output, "model.c4");
        await File.AppendAllTextAsync(target, "// human", TestContext.Current.CancellationToken);
        GenerationPlan edited = await ManagedOutputManager.PreviewAsync(output, "1.0", files, TestContext.Current.CancellationToken);
        Assert.True(edited.HasConflicts);
        Assert.Equal(GeneratedFileChangeKind.Conflict, Assert.Single(edited.Changes).Kind);
        Assert.EndsWith("// human", await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken), StringComparison.Ordinal);

        string unmanagedOutput = Path.Combine(temp.Path, "unmanaged");
        Directory.CreateDirectory(unmanagedOutput);
        string unmanaged = Path.Combine(unmanagedOutput, "model.c4");
        await File.WriteAllTextAsync(unmanaged, "human", TestContext.Current.CancellationToken);
        GenerationPlan unmanagedPlan = await ManagedOutputManager.PreviewAsync(unmanagedOutput, "1.0", files, TestContext.Current.CancellationToken);
        Assert.True(unmanagedPlan.HasConflicts);
        Assert.Equal("human", await File.ReadAllTextAsync(unmanaged, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingPreviouslyManagedFileIsConflict()
    {
        using TempDirectory temp = new();
        string output = Path.Combine(temp.Path, "out");
        LikeC4GeneratedFile[] files = [new("model.c4", "model { }\n")];
        GenerationPlan first = await ManagedOutputManager.PreviewAsync(output, "1.0", files, TestContext.Current.CancellationToken);
        await ManagedOutputManager.CommitAsync(output, "1.0", files, first, TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(output, "model.c4"));

        GenerationPlan plan = await ManagedOutputManager.PreviewAsync(output, "1.0", files, TestContext.Current.CancellationToken);

        Assert.True(plan.HasConflicts);
    }

    [Fact]
    public async Task FailedManifestCommitRollsBackNewlyWrittenOutputs()
    {
        using TempDirectory temp = new();
        string output = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(output);
        string manifestPath = Path.Combine(output, ManagedOutputManager.ManifestFileName);
        Directory.CreateDirectory(manifestPath);
        LikeC4GeneratedFile[] files = [new("model.c4", "generated")];

        GenerationPlan preview = await ManagedOutputManager.PreviewAsync(
            output,
            "1.0",
            files,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<IOException>(
            () => ManagedOutputManager.CommitAsync(
                output,
                "1.0",
                files,
                preview,
                TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Path.Combine(output, "model.c4")));
        Assert.True(Directory.Exists(manifestPath));
    }

    [Fact]
    public async Task RemovedManagedOutputIsDeletedAndCanBeRegenerated()
    {
        using TempDirectory temp = new();
        string output = Path.Combine(temp.Path, "out");
        LikeC4GeneratedFile[] initial =
        [
            new("model.c4", "model { }"),
            new("c3.views.c4", "views { }"),
        ];
        GenerationPlan first = await ManagedOutputManager.PreviewAsync(
            output, "1.0", initial, TestContext.Current.CancellationToken);
        await ManagedOutputManager.CommitAsync(
            output, "1.0", initial, first, TestContext.Current.CancellationToken);

        LikeC4GeneratedFile[] withoutC3 = [new("model.c4", "model { }")];
        GenerationPlan removal = await ManagedOutputManager.PreviewAsync(
            output, "1.0", withoutC3, TestContext.Current.CancellationToken);
        Assert.Equal(
            GeneratedFileChangeKind.Removed,
            Assert.Single(removal.Changes, change => change.FileName == "c3.views.c4").Kind);
        Assert.True(File.Exists(Path.Combine(output, "c3.views.c4")));

        await ManagedOutputManager.CommitAsync(
            output, "1.0", withoutC3, removal, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(output, "c3.views.c4")));

        GenerationPlan next = await ManagedOutputManager.PreviewAsync(
            output, "1.0", initial, TestContext.Current.CancellationToken);
        Assert.Equal(
            GeneratedFileChangeKind.Added,
            Assert.Single(next.Changes, change => change.FileName == "c3.views.c4").Kind);
    }

    [Fact]
    public async Task EditedStaleOutputBlocksRemovalAndLeavesManifestIntact()
    {
        using TempDirectory temp = new();
        string output = Path.Combine(temp.Path, "out");
        LikeC4GeneratedFile[] initial =
        [
            new("model.c4", "model { }"),
            new("c3.views.c4", "original"),
        ];
        GenerationPlan first = await ManagedOutputManager.PreviewAsync(
            output, "1.0", initial, TestContext.Current.CancellationToken);
        await ManagedOutputManager.CommitAsync(
            output, "1.0", initial, first, TestContext.Current.CancellationToken);

        string manifest = Path.Combine(output, ManagedOutputManager.ManifestFileName);
        string previous = await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken);
        string c3File = Path.Combine(output, "c3.views.c4");
        await File.WriteAllTextAsync(c3File, "human edit", TestContext.Current.CancellationToken);

        LikeC4GeneratedFile[] next = [new("model.c4", "updated")];
        GenerationPlan conflict = await ManagedOutputManager.PreviewAsync(
            output, "1.0", next, TestContext.Current.CancellationToken);
        Assert.Equal(
            GeneratedFileChangeKind.Conflict,
            Assert.Single(conflict.Changes, change => change.FileName == "c3.views.c4").Kind);
        await Assert.ThrowsAnyAsync<IOException>(
            () => ManagedOutputManager.CommitAsync(
                output, "1.0", next, conflict, TestContext.Current.CancellationToken));
        Assert.Equal("human edit", await File.ReadAllTextAsync(c3File, TestContext.Current.CancellationToken));
        Assert.Equal(previous, await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("{not valid")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":\"1.0\",\"ModelSchemaVersion\":\"1.0\"}")]
    [InlineData("{\"SchemaVersion\":\"1.0\",\"ModelSchemaVersion\":\"1.0\",\"Files\":[null]}")]
    [InlineData("{\"SchemaVersion\":\"1.0\",\"ModelSchemaVersion\":\"1.0\",\"Files\":[{\"FileName\":\"model.c4\",\"Sha256\":\"\"}]}")]
    public async Task InvalidManifestProducesControlledIOException(string json)
    {
        using TempDirectory temp = new();
        string output = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(
            Path.Combine(output, ManagedOutputManager.ManifestFileName),
            json,
            TestContext.Current.CancellationToken);

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => ManagedOutputManager.PreviewAsync(
                output,
                "1.0",
                [new LikeC4GeneratedFile("model.c4", "model { }")],
                TestContext.Current.CancellationToken));
        Assert.Equal("managed_output_manifest_invalid", exception.Message);
    }

    [Fact]
    public async Task InvalidPathIsRejected()
    {
        using TempDirectory temp = new();
        LikeC4GeneratedFile[] files = [new("../outside.c4", "x")];

        await Assert.ThrowsAnyAsync<IOException>(
            () => ManagedOutputManager.PreviewAsync(
                Path.Combine(temp.Path, "out"),
                "1.0",
                files,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConflictPreventsCommitAndManifestUpdate()
    {
        using TempDirectory temp = new();
        string output = Path.Combine(temp.Path, "out");
        LikeC4GeneratedFile[] files = [new("model.c4", "old")];
        GenerationPlan first = await ManagedOutputManager.PreviewAsync(output, "1.0", files, TestContext.Current.CancellationToken);
        await ManagedOutputManager.CommitAsync(output, "1.0", files, first, TestContext.Current.CancellationToken);

        string manifest = Path.Combine(output, ManagedOutputManager.ManifestFileName);
        string manifestBefore = await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(output, "model.c4"), "manual", TestContext.Current.CancellationToken);
        LikeC4GeneratedFile[] changed = [new("model.c4", "new")];
        GenerationPlan conflict = await ManagedOutputManager.PreviewAsync(
            output,
            "1.0",
            changed,
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<IOException>(
            () => ManagedOutputManager.CommitAsync(
                output,
                "1.0",
                changed,
                conflict,
                TestContext.Current.CancellationToken));

        Assert.Equal(
            "manual",
            await File.ReadAllTextAsync(
                Path.Combine(output, "model.c4"),
                TestContext.Current.CancellationToken));
        Assert.Equal(
            manifestBefore,
            await File.ReadAllTextAsync(
                manifest,
                TestContext.Current.CancellationToken));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = Directory
                .CreateTempSubdirectory("repo2c4-managed-output-")
                .FullName;
        }

        public string Path
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
