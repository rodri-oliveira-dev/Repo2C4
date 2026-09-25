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
        GenerationPlan first = await ManagedOutputManager.PreviewAsync(output, "1.0", files);
        await ManagedOutputManager.CommitAsync(output, "1.0", files, first);

        GenerationPlan second = await ManagedOutputManager.PreviewAsync(output, "1.0", files);

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
        GenerationPlan first = await ManagedOutputManager.PreviewAsync(output, "1.0", files);
        await ManagedOutputManager.CommitAsync(output, "1.0", files, first);

        string target = Path.Combine(output, "model.c4");
        await File.AppendAllTextAsync(target, "// human");
        GenerationPlan edited = await ManagedOutputManager.PreviewAsync(output, "1.0", files);
        Assert.True(edited.HasConflicts);
        Assert.Equal(GeneratedFileChangeKind.Conflict, Assert.Single(edited.Changes).Kind);
        Assert.EndsWith("// human", await File.ReadAllTextAsync(target), StringComparison.Ordinal);

        string unmanagedOutput = Path.Combine(temp.Path, "unmanaged");
        Directory.CreateDirectory(unmanagedOutput);
        string unmanaged = Path.Combine(unmanagedOutput, "model.c4");
        await File.WriteAllTextAsync(unmanaged, "human");
        GenerationPlan unmanagedPlan = await ManagedOutputManager.PreviewAsync(unmanagedOutput, "1.0", files);
        Assert.True(unmanagedPlan.HasConflicts);
        Assert.Equal("human", await File.ReadAllTextAsync(unmanaged));
    }

    [Fact]
    public async Task MissingPreviouslyManagedFileIsConflict()
    {
        using TempDirectory temp = new();
        string output = Path.Combine(temp.Path, "out");
        LikeC4GeneratedFile[] files = [new("model.c4", "model { }\n")];
        GenerationPlan first = await ManagedOutputManager.PreviewAsync(output, "1.0", files);
        await ManagedOutputManager.CommitAsync(output, "1.0", files, first);
        File.Delete(Path.Combine(output, "model.c4"));

        GenerationPlan plan = await ManagedOutputManager.PreviewAsync(output, "1.0", files);

        Assert.True(plan.HasConflicts);
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
        GenerationPlan first = await ManagedOutputManager.PreviewAsync(output, "1.0", files);
        await ManagedOutputManager.CommitAsync(output, "1.0", files, first);

        string manifest = Path.Combine(output, ManagedOutputManager.ManifestFileName);
        string manifestBefore = await File.ReadAllTextAsync(manifest);
        await File.WriteAllTextAsync(Path.Combine(output, "model.c4"), "manual");
        LikeC4GeneratedFile[] changed = [new("model.c4", "new")];
        GenerationPlan conflict = await ManagedOutputManager.PreviewAsync(output, "1.0", changed);

        await Assert.ThrowsAnyAsync<IOException>(
            () => ManagedOutputManager.CommitAsync(
                output,
                "1.0",
                changed,
                conflict,
                TestContext.Current.CancellationToken));

        Assert.Equal("manual", await File.ReadAllTextAsync(Path.Combine(output, "model.c4")));
        Assert.Equal(manifestBefore, await File.ReadAllTextAsync(manifest));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Path = Directory.CreateTempSubdirectory("repo2c4-managed-output-").FullName;

        public string Path { get; }

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
