using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Repo2C4.Core.LikeC4;

/// <summary>Controlled invocation settings selected by the host, never by repository source content.</summary>
public sealed record LikeC4CliValidationOptions(string ExecutablePath, TimeSpan Timeout)
{
    public static LikeC4CliValidationOptions Default { get; } = new("likec4", TimeSpan.FromSeconds(30));
}

/// <summary>A bounded, non-source-bearing validation diagnostic.</summary>
public sealed record LikeC4ValidationDiagnostic(string Code, string? RelativePath, string Message);

/// <summary>Result of one official LikeC4 CLI validation process.</summary>
public sealed record LikeC4ValidationResult(
    bool IsValid,
    int ExitCode,
    bool TimedOut,
    ImmutableArray<LikeC4ValidationDiagnostic> Diagnostics);

/// <summary>
/// Invokes only the official LikeC4 validation command in a caller-selected workspace.
/// Repository source content cannot select a command, executable or extra arguments.
/// </summary>
public static partial class LikeC4CliValidator
{
    public const int WorkspaceErrorExitCode = 2;
    public const int TimeoutExitCode = 124;
    public const int CliUnavailableExitCode = 127;

    private const int MaxCapturedCharactersPerStream = 32_768;
    private const int MaxDiagnosticFiles = 16;

    public static async Task<LikeC4ValidationResult> ValidateAsync(
        string workspacePath,
        LikeC4CliValidationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            throw new ArgumentException("LikeC4 workspace path is required.", nameof(workspacePath));
        }

        options ??= LikeC4CliValidationOptions.Default;
        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            throw new ArgumentException("LikeC4 executable path is required.", nameof(options));
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "LikeC4 validation timeout must be positive.");
        }

        string fullWorkspace;
        try
        {
            fullWorkspace = Path.GetFullPath(workspacePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(
                WorkspaceErrorExitCode,
                "likec4.workspaceInvalid",
                null,
                "LikeC4 workspace path is invalid.");
        }

        if (!Directory.Exists(fullWorkspace))
        {
            return Failure(
                WorkspaceErrorExitCode,
                "likec4.workspaceMissing",
                null,
                "LikeC4 workspace directory does not exist.");
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = options.ExecutablePath,
            WorkingDirectory = fullWorkspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("validate");
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["FORCE_COLOR"] = "0";

        using Process process = new()
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        try
        {
            if (!process.Start())
            {
                return CliUnavailable(options.ExecutablePath);
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception
                or FileNotFoundException
                or InvalidOperationException)
        {
            return CliUnavailable(options.ExecutablePath);
        }

        Task<string> stdoutTask = ReadBoundedAsync(process.StandardOutput);
        Task<string> stderrTask = ReadBoundedAsync(process.StandardError);

        using CancellationTokenSource timeoutSource = new(options.Timeout);
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            await DrainAfterTerminationAsync(process, stdoutTask, stderrTask).ConfigureAwait(false);

            return new LikeC4ValidationResult(
                false,
                TimeoutExitCode,
                true,
                [
                    new LikeC4ValidationDiagnostic(
                        "likec4.timeout",
                        null,
                        "LikeC4 validation exceeded the configured timeout and was terminated."),
                ]);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode == 0)
        {
            return new LikeC4ValidationResult(true, 0, false, []);
        }

        ImmutableArray<LikeC4ValidationDiagnostic> diagnostics =
            BuildValidationDiagnostics(fullWorkspace, stdout, stderr);

        return new LikeC4ValidationResult(false, process.ExitCode, false, diagnostics);
    }

    private static LikeC4ValidationResult CliUnavailable(string executablePath)
    {
        string executableName = Path.GetFileName(executablePath);
        if (string.IsNullOrWhiteSpace(executableName))
        {
            executableName = "likec4";
        }

        return Failure(
            CliUnavailableExitCode,
            "likec4.cliUnavailable",
            null,
            "LikeC4 CLI executable '" + executableName
                + "' is unavailable. Install the documented pinned LikeC4 version and retry.");
    }

    private static LikeC4ValidationResult Failure(
        int exitCode,
        string code,
        string? relativePath,
        string message) =>
        new(
            false,
            exitCode,
            false,
            [new LikeC4ValidationDiagnostic(code, relativePath, message)]);

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        StringBuilder captured = new();

        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (captured.Length >= MaxCapturedCharactersPerStream)
            {
                continue;
            }

            int remaining = MaxCapturedCharactersPerStream - captured.Length;
            if (line.Length > remaining)
            {
                captured.Append(line.AsSpan(0, remaining));
                continue;
            }

            captured.AppendLine(line);
        }

        return captured.ToString();
    }

    private static async Task DrainAfterTerminationAsync(
        Process process,
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process may already be gone after a failed start/termination race.
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Streams can close abruptly after killing an external process.
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
        catch (Exception exception) when (
            exception is InvalidOperationException
                or NotSupportedException
                or Win32Exception)
        {
            // Timeout is already the primary diagnostic; do not replace it with process cleanup details.
        }
    }

    private static ImmutableArray<LikeC4ValidationDiagnostic> BuildValidationDiagnostics(
        string workspace,
        string stdout,
        string stderr)
    {
        HashSet<string> paths = new(StringComparer.Ordinal);
        ExtractPaths(workspace, stderr, paths);
        ExtractPaths(workspace, stdout, paths);

        if (paths.Count == 0)
        {
            return
            [
                new LikeC4ValidationDiagnostic(
                    "likec4.validationFailed",
                    null,
                    "LikeC4 validation failed with a non-zero exit code. Run 'likec4 validate' locally in the generated workspace for the full diagnostic."),
            ];
        }

        return
        [
            .. paths
                .OrderBy(path => path, StringComparer.Ordinal)
                .Take(MaxDiagnosticFiles)
                .Select(path => new LikeC4ValidationDiagnostic(
                    "likec4.validationError",
                    path,
                    "LikeC4 reported a validation error in this source file. Run 'likec4 validate' locally in the generated workspace for the full diagnostic.")),
        ];
    }

    private static void ExtractPaths(string workspace, string output, HashSet<string> paths)
    {
        foreach (Match match in LikeC4PathRegex().Matches(output))
        {
            string candidate = match.Groups["path"].Value.Replace('\\', '/');
            string? relative = NormalizeReportedPath(workspace, candidate);
            if (relative is not null)
            {
                paths.Add(relative);
            }

            if (paths.Count >= MaxDiagnosticFiles)
            {
                return;
            }
        }
    }

    private static string? NormalizeReportedPath(string workspace, string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            string fullCandidate = Path.IsPathFullyQualified(candidate)
                ? Path.GetFullPath(candidate)
                : Path.GetFullPath(Path.Combine(workspace, candidate.TrimStart('.', '/', '\\')));

            string relative = Path.GetRelativePath(workspace, fullCandidate).Replace('\\', '/');
            return IsSafeRelativePath(relative) && File.Exists(fullCandidate) ? relative : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsSafeRelativePath(string path) =>
        path.Length > 0
        && !path.StartsWith("../", StringComparison.Ordinal)
        && path != ".."
        && !path.Contains(':')
        && !path.Any(char.IsControl);

    [GeneratedRegex(
        @"(?<path>[A-Za-z0-9_./\\-]+\.(?:c4|likec4))(?::\d+(?::\d+)?)?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex LikeC4PathRegex();
}
