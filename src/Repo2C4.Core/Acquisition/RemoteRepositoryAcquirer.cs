using System.Diagnostics;

namespace Repo2C4.Core.Acquisition;

public sealed record RemoteRepositoryRequest(
    string Url,
    string? Ref = null,
    TimeSpan? Timeout = null,
    int MaxFiles = 20_000,
    long MaxBytes = 128L * 1024 * 1024);

public sealed record RemoteRepositoryProvenance(string Url, string? Ref, string Commit)
{
    public string CreateRepositoryId()
    {
        string identity = Url.ToLowerInvariant() + "\n" + Commit.ToLowerInvariant();
        byte[] digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity));
        return "repo_" + Convert.ToHexString(digest).ToLowerInvariant()[..24];
    }
}

public sealed class RemoteRepositoryException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public sealed class RemoteRepositoryWorkspace : IAsyncDisposable
{
    internal RemoteRepositoryWorkspace(string rootPath, RemoteRepositoryProvenance provenance)
    {
        RootPath = rootPath;
        Provenance = provenance;
    }

    public string RootPath
    {
        get;
    }

    public RemoteRepositoryProvenance Provenance
    {
        get;
    }

    public ValueTask DisposeAsync()
    {
        RemoteRepositoryAcquirer.TryCleanupWorkspace(RootPath);
        return ValueTask.CompletedTask;
    }
}

public interface IGitProcessRunner
{
    Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public sealed record GitProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class GitProcessRunner : IGitProcessRunner
{
    public async Task<GitProcessResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_ASKPASS"] = string.Empty;
        process.StartInfo.Environment["GCM_INTERACTIVE"] = "Never";
        process.StartInfo.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
        process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        process.StartInfo.Environment.Remove("GIT_DIR");
        process.StartInfo.Environment.Remove("GIT_WORK_TREE");
        process.StartInfo.Environment.Remove("GIT_INDEX_FILE");
        process.StartInfo.Environment.Remove("GIT_OBJECT_DIRECTORY");
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                throw new RemoteRepositoryException("git_unavailable", "Git could not be started.");
            }

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            using CancellationTokenSource timeoutSource = new(timeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new RemoteRepositoryException("git_timeout", "Git operation exceeded the configured timeout.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            return new GitProcessResult(
                process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new RemoteRepositoryException("git_unavailable", "Git executable is unavailable.", exception);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed class RemoteRepositoryAcquirer(IGitProcessRunner? git = null)
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private readonly IGitProcessRunner _git = git ?? new GitProcessRunner();

    public async Task<RemoteRepositoryWorkspace> AcquireAsync(
        RemoteRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Uri uri = ValidateUrl(request.Url);
        ValidateRef(request.Ref);
        ValidateLimits(request);

        string workspace = Directory.CreateTempSubdirectory("repo2c4-remote-").FullName;

        try
        {
            TimeSpan timeout = request.Timeout ?? DefaultTimeout;
            await RunRequiredAsync(workspace, timeout, cancellationToken, "init", "--quiet").ConfigureAwait(false);
            await RunRequiredAsync(
                workspace,
                timeout,
                cancellationToken,
                "-c",
                "credential.helper=",
                "-c",
                "protocol.allow=never",
                "-c",
                "protocol.https.allow=always",
                "-c",
                "protocol.file.allow=never",
                "-c",
                "submodule.recurse=false",
                "fetch",
                "--quiet",
                "--depth=1",
                "--no-tags",
                uri.AbsoluteUri,
                request.Ref ?? "HEAD").ConfigureAwait(false);

            string commit = (await RunRequiredAsync(
                workspace,
                timeout,
                cancellationToken,
                "rev-parse",
                "--verify",
                "FETCH_HEAD^{commit}").ConfigureAwait(false)).Trim();
            if (commit.Length != 40 || !commit.All(Uri.IsHexDigit))
            {
                throw new RemoteRepositoryException("git_commit_invalid", "Git did not resolve a valid commit.");
            }

            string tree = await RunRequiredAsync(
                workspace,
                timeout,
                cancellationToken,
                "ls-tree",
                "-r",
                "-l",
                "-z",
                "--full-tree",
                commit).ConfigureAwait(false);
            RejectUnsafeTree(tree, request.MaxFiles, request.MaxBytes);

            await RunRequiredAsync(
                workspace,
                timeout,
                cancellationToken,
                "-c",
                "core.hooksPath=" + Path.Combine(workspace, ".git", "repo2c4-no-hooks"),
                "-c",
                "submodule.recurse=false",
                "checkout",
                "--quiet",
                "--detach",
                commit).ConfigureAwait(false);

            RejectUnsafeMaterialization(workspace, request.MaxFiles, request.MaxBytes);
            return new RemoteRepositoryWorkspace(
                workspace,
                new RemoteRepositoryProvenance(uri.AbsoluteUri, request.Ref, commit.ToLowerInvariant()));
        }
        catch
        {
            TryCleanupWorkspace(workspace);
            throw;
        }
    }

    public static Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new RemoteRepositoryException("remote_url_invalid", "Remote repository URL must be an absolute public HTTPS URL.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new RemoteRepositoryException("remote_credentials_rejected", "Embedded credentials and authenticated repositories are not supported.");
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            throw new RemoteRepositoryException("remote_url_invalid", "Remote repository URL must not contain a fragment.");
        }

        return uri;
    }

    private static void ValidateRef(string? reference)
    {
        if (reference is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(reference)
            || reference.Length > 256
            || reference.StartsWith('-')
            || reference.Any(char.IsControl)
            || reference.Contains("..", StringComparison.Ordinal)
            || reference.Contains("@{", StringComparison.Ordinal)
            || reference.EndsWith('.')
            || reference.EndsWith('/')
            || reference.Contains(' ')
            || reference.IndexOfAny([':', '*', '^', '~', '?', '[', '\\']) >= 0
            || reference.Contains("//", StringComparison.Ordinal)
            || reference.EndsWith(".lock", StringComparison.Ordinal))
        {
            throw new RemoteRepositoryException("remote_ref_invalid", "Remote Git ref is invalid.");
        }
    }

    private static void ValidateLimits(RemoteRepositoryRequest request)
    {
        if (request.MaxFiles is < 1 or > 100_000 || request.MaxBytes is < 1 or > 1024L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Remote acquisition limits are invalid.");
        }

        if (request.Timeout is { } timeout && (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Remote acquisition timeout must be between 1 ms and 5 minutes.");
        }
    }

    private async Task<string> RunRequiredAsync(
        string workspace,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        GitProcessResult result = await _git.RunAsync(
            workspace,
            arguments,
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            string operation = arguments.Contains("fetch", StringComparer.Ordinal) ? "fetch" : "operation";
            throw new RemoteRepositoryException(
                operation == "fetch" ? "remote_fetch_failed" : "git_process_failed",
                operation == "fetch"
                    ? "Remote repository could not be fetched. Confirm that it is public and the ref exists."
                    : "Git process failed while preparing the isolated workspace.");
        }

        return result.StandardOutput;
    }

    private static void RejectUnsafeTree(string tree, int maxFiles, long maxBytes)
    {
        int files = 0;
        long bytes = 0;
        foreach (string entry in tree.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = entry.IndexOf('\t');
            if (tab <= 0)
            {
                throw new RemoteRepositoryException("git_tree_invalid", "Git tree metadata is invalid.");
            }

            string metadata = entry[..tab];
            string path = entry[(tab + 1)..].Replace('\\', '/');
            string[] parts = metadata.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 || !long.TryParse(parts[3], out long size))
            {
                throw new RemoteRepositoryException("git_tree_invalid", "Git tree metadata is invalid.");
            }

            string mode = parts[0];
            if (mode is "120000" or "160000")
            {
                throw new RemoteRepositoryException("repository_link_rejected", "Remote repository contains unsupported links or gitlinks.");
            }

            if (string.Equals(path, ".gitmodules", StringComparison.Ordinal))
            {
                throw new RemoteRepositoryException("submodules_rejected", "Repositories declaring Git submodules are not supported.");
            }

            files++;
            bytes += size;
            if (files > maxFiles || bytes > maxBytes)
            {
                throw new RemoteRepositoryException("remote_limit_exceeded", "Remote repository exceeds acquisition file or size limits.");
            }
        }
    }

    private static void RejectUnsafeMaterialization(string workspace, int maxFiles, long maxBytes)
    {
        string gitModules = Path.Combine(workspace, ".gitmodules");
        if (File.Exists(gitModules))
        {
            throw new RemoteRepositoryException("submodules_rejected", "Repositories declaring Git submodules are not supported.");
        }

        int files = 0;
        long bytes = 0;
        foreach (string entry in Directory.EnumerateFileSystemEntries(
            workspace,
            "*",
            SearchOption.AllDirectories))
        {
            if (entry.StartsWith(Path.Combine(workspace, ".git") + Path.DirectorySeparatorChar, PathComparison))
            {
                continue;
            }

            FileAttributes attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new RemoteRepositoryException("repository_link_rejected", "Remote repository contains a symbolic link or reparse point.");
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                continue;
            }

            files++;
            bytes += new FileInfo(entry).Length;
            if (files > maxFiles || bytes > maxBytes)
            {
                throw new RemoteRepositoryException("remote_limit_exceeded", "Remote repository exceeds acquisition file or size limits.");
            }
        }
    }

    internal static void TryCleanupWorkspace(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
