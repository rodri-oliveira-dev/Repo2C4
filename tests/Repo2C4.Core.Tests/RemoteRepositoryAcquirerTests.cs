using Repo2C4.Core.Acquisition;
using Xunit;

namespace Repo2C4.Core.Tests;

public sealed class RemoteRepositoryAcquirerTests
{
    [Theory]
    [InlineData("http://example.com/repo.git", "remote_url_invalid")]
    [InlineData("ssh://example.com/repo.git", "remote_url_invalid")]
    [InlineData("file:///tmp/repo", "remote_url_invalid")]
    [InlineData("https://user:secret@example.com/repo.git", "remote_credentials_rejected")]
    public async Task RejectsUnsafeRemoteUrls(string url, string code)
    {
        RemoteRepositoryAcquirer acquirer = new(new ScriptedGitRunner(Array.Empty<GitProcessResult>()));
        RemoteRepositoryException exception = await Assert.ThrowsAsync<RemoteRepositoryException>(
            () => acquirer.AcquireAsync(new RemoteRepositoryRequest(url), TestContext.Current.CancellationToken));
        Assert.Equal(code, exception.Code);
    }

    [Fact]
    public async Task ReportsNonexistentRefWithoutLeakingGitOutput()
    {
        GitProcessResult[] results =
        [
            new GitProcessResult(0, string.Empty, string.Empty),
            new GitProcessResult(128, string.Empty, "fatal: https://user:secret@example.invalid/private"),
        ];
        ScriptedGitRunner runner = new(results);
        RemoteRepositoryAcquirer acquirer = new(runner);

        RemoteRepositoryException exception = await Assert.ThrowsAsync<RemoteRepositoryException>(
            () => acquirer.AcquireAsync(
                new RemoteRepositoryRequest("https://example.invalid/repo.git", "missing"),
                TestContext.Current.CancellationToken));

        Assert.Equal("remote_fetch_failed", exception.Code);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PropagatesCancellationAndCleansWorkspace()
    {
        CancellingGitRunner runner = new();
        RemoteRepositoryAcquirer acquirer = new(runner);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => acquirer.AcquireAsync(
                new RemoteRepositoryRequest("https://example.invalid/repo.git"),
                cancellation.Token));

        Assert.NotNull(runner.WorkingDirectory);
        Assert.False(Directory.Exists(runner.WorkingDirectory));
    }

    [Fact]
    public async Task PropagatesTimeoutFailureAndCleansWorkspace()
    {
        FailingGitRunner runner = new(new RemoteRepositoryException("git_timeout", "timed out"));
        RemoteRepositoryAcquirer acquirer = new(runner);

        RemoteRepositoryException exception = await Assert.ThrowsAsync<RemoteRepositoryException>(
            () => acquirer.AcquireAsync(new RemoteRepositoryRequest("https://example.invalid/repo.git"), TestContext.Current.CancellationToken));

        Assert.Equal("git_timeout", exception.Code);
        Assert.False(Directory.Exists(runner.WorkingDirectory));
    }

    [Fact]
    public async Task RejectsSubmoduleDeclarationAndCleansWorkspace()
    {
        MaterializingGitRunner runner = new(static root =>
            File.WriteAllText(Path.Combine(root, ".gitmodules"), "[submodule \"x\"]"));
        RemoteRepositoryAcquirer acquirer = new(runner);

        RemoteRepositoryException exception = await Assert.ThrowsAsync<RemoteRepositoryException>(
            () => acquirer.AcquireAsync(new RemoteRepositoryRequest("https://example.invalid/repo.git"), TestContext.Current.CancellationToken));

        Assert.Equal("submodules_rejected", exception.Code);
        Assert.False(Directory.Exists(runner.WorkingDirectory));
    }

    [Fact]
    public async Task RejectsExceededFileLimitAndCleansWorkspace()
    {
        MaterializingGitRunner runner = new(static root =>
        {
            File.WriteAllText(Path.Combine(root, "a.cs"), "a");
            File.WriteAllText(Path.Combine(root, "b.cs"), "b");
        });
        RemoteRepositoryAcquirer acquirer = new(runner);

        RemoteRepositoryException exception = await Assert.ThrowsAsync<RemoteRepositoryException>(
            () => acquirer.AcquireAsync(
                new RemoteRepositoryRequest("https://example.invalid/repo.git", MaxFiles: 1), TestContext.Current.CancellationToken));

        Assert.Equal("remote_limit_exceeded", exception.Code);
        Assert.False(Directory.Exists(runner.WorkingDirectory));
    }

    [Fact]
    public async Task RejectsSymbolicLinksWhenSupported()
    {
        bool linkCreated = false;
        MaterializingGitRunner runner = new(root =>
        {
            string target = Path.Combine(root, "target.txt");
            File.WriteAllText(target, "safe");
            try
            {
                File.CreateSymbolicLink(Path.Combine(root, "link.txt"), target);
                linkCreated = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return;
            }
        });
        RemoteRepositoryAcquirer acquirer = new(runner);

        try
        {
            RemoteRepositoryWorkspace workspace = await acquirer.AcquireAsync(
                new RemoteRepositoryRequest("https://example.invalid/repo.git"), TestContext.Current.CancellationToken);
            await workspace.DisposeAsync();
            Assert.False(linkCreated, "Symbolic link was materialized but not rejected.");
        }
        catch (RemoteRepositoryException exception)
        {
            Assert.Equal("repository_link_rejected", exception.Code);
            Assert.False(Directory.Exists(runner.WorkingDirectory));
        }
    }

    [Theory]
    [InlineData("+refs/*:refs/remotes/x/*")]
    [InlineData("^refs/heads/main")]
    [InlineData("refs//heads/main")]
    [InlineData("refs/heads/main.lock")]
    public async Task RejectsRefspecSyntax(string reference)
    {
        RemoteRepositoryAcquirer acquirer = new(new ScriptedGitRunner(Array.Empty<GitProcessResult>()));

        RemoteRepositoryException exception = await Assert.ThrowsAsync<RemoteRepositoryException>(
            () => acquirer.AcquireAsync(
                new RemoteRepositoryRequest("https://example.invalid/repo.git", reference),
                TestContext.Current.CancellationToken));

        Assert.Equal("remote_ref_invalid", exception.Code);
    }

    [Fact]
    public async Task SuccessfulAcquisitionRecordsSanitizedProvenanceAndCleansOnDispose()
    {
        MaterializingGitRunner runner = new(static root =>
            File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project />"));
        RemoteRepositoryAcquirer acquirer = new(runner);

        RemoteRepositoryWorkspace workspace = await acquirer.AcquireAsync(
            new RemoteRepositoryRequest("https://example.invalid/repo.git", "refs/heads/main"), TestContext.Current.CancellationToken);

        Assert.Equal("https://example.invalid/repo.git", workspace.Provenance.Url);
        Assert.Equal("refs/heads/main", workspace.Provenance.Ref);
        Assert.Equal(new string('a', 40), workspace.Provenance.Commit);
        Assert.True(Directory.Exists(workspace.RootPath));

        string path = workspace.RootPath;
        await workspace.DisposeAsync();
        Assert.False(Directory.Exists(path));
    }

    private sealed class ScriptedGitRunner(IEnumerable<GitProcessResult> results) : IGitProcessRunner
    {
        private readonly Queue<GitProcessResult> _results = new(results);
        public Task<GitProcessResult> RunAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class CancellingGitRunner : IGitProcessRunner
    {
        public string? WorkingDirectory
        {
            get;
            private set;
        }

        public Task<GitProcessResult> RunAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            WorkingDirectory = workingDirectory;
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class FailingGitRunner(Exception exception) : IGitProcessRunner
    {
        public string WorkingDirectory
        {
            get;
            private set;
        } = string.Empty;

        public Task<GitProcessResult> RunAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            WorkingDirectory = workingDirectory;
            throw exception;
        }
    }

    private sealed class MaterializingGitRunner(Action<string> materialize) : IGitProcessRunner
    {
        private int _call;

        public string WorkingDirectory { get; private set; } = string.Empty;

        public Task<GitProcessResult> RunAsync(
            string workingDirectory,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            WorkingDirectory = workingDirectory;
            cancellationToken.ThrowIfCancellationRequested();
            _call++;
            if (_call == 4)
            {
                materialize(workingDirectory);
            }

            string output = _call == 3 ? new string('a', 40) + "\n" : string.Empty;
            return Task.FromResult(new GitProcessResult(0, output, string.Empty));
        }
    }
}
