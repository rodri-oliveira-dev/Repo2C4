using System.Collections.Immutable;
using System.Security;
using System.Text.RegularExpressions;
using Repo2C4.Core.Contracts;

namespace Repo2C4.Core.Inspection;

/// <summary>
/// Deterministic, bounded metadata inventory of one explicitly authorized local repository.
/// Never executes repository content, returns source bodies, or follows reparse points.
/// </summary>
public sealed class RepositoryScanner
{
    private const int MaxPatterns = 64;
    private const int MaxPatternLength = 256;
    private const int BinaryProbeBytes = 4_096;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".idea", ".secrets", ".terraform", ".nuget",
        ".dotnet", ".cache", "bin", "obj", "node_modules", "vendor", "artifacts",
        "dist", "build", "coverage", "TestResults",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets", ".json", ".jsonc",
        ".xml", ".yaml", ".yml", ".md", ".txt", ".config", ".editorconfig",
        ".gitignore", ".gitattributes", ".sh", ".ps1", ".toml", ".ini", ".razor",
        ".resx", ".http", ".graphql", ".proto", ".sql", ".fs", ".fsproj",
    };

    /// <summary>
    /// Scans metadata synchronously; callers can cancel and serialize the returned v1 snapshot.
    /// Enumeration is capped and omitted-entry counts are lower bounds if enumeration stops early.
    /// </summary>
    public static RepositorySnapshot Scan(RepositoryScanOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(options);

        string root = Path.GetFullPath(options.RootPath);
        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(root);
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            throw new ArgumentException("Authorized repository root is not accessible.", nameof(options), exception);
        }

        if (!rootAttributes.HasFlag(FileAttributes.Directory) || rootAttributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ArgumentException("Authorized root must be a real, non-linked directory.", nameof(options));
        }

        // Invalid repository IDs cannot be emitted in a v1 snapshot, and must not expose root paths.
        RepositorySnapshot empty = new(ContractSchema.Version, options.RepositoryId, [], [], []);
        if (!ContractValidator.ValidateSnapshot(empty).IsEmpty)
        {
            throw new ArgumentException("RepositoryId must be a valid stable v1 identifier.", nameof(options));
        }

        ScanState state = new(options, cancellationToken);
        VisitDirectory(root, root, state);

        List<RepositoryDiagnostic> diagnostics = [];
        foreach ((string code, int count) in state.Omissions.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            diagnostics.Add(new RepositoryDiagnostic(code, DiagnosticSeverity.Warning, null,
                "Skipped " + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " observed entries (" + code + ")."));
        }

        if (state.EntryBudgetReached)
        {
            diagnostics.Add(new RepositoryDiagnostic("scan.entryLimit", DiagnosticSeverity.Warning, null,
                "The entry budget was reached. Additional entries were not inspected; omission counts are lower bounds."));
        }

        RepositorySnapshot snapshot = new(
            ContractSchema.Version,
            options.RepositoryId,
            [.. state.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal)],
            [],
            [.. diagnostics.OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)]);

        // The inspector must emit a snapshot that all future consumers can validate.
        ImmutableArray<ContractError> errors = ContractValidator.ValidateSnapshot(snapshot);
        if (!errors.IsEmpty)
        {
            throw new ContractValidationException(errors);
        }

        return snapshot;
    }

    private static void VisitDirectory(string root, string directory, ScanState state)
    {
        state.CancellationToken.ThrowIfCancellationRequested();
        if (state.EntryBudgetReached)
        {
            return;
        }

        List<string> children = [];
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                state.CancellationToken.ThrowIfCancellationRequested();
                if (state.VisitedEntries + children.Count >= state.Options.MaxVisitedEntries)
                {
                    state.EntryBudgetReached = true;
                    break;
                }

                children.Add(entry);
            }
        }
        catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
        {
            state.Skip(exception is UnauthorizedAccessException or SecurityException ? "scan.accessDenied" : "scan.unavailable");
        }

        // A stable traversal order makes ordinary scans independent of directory enumeration order.
        foreach (string entry in children.OrderBy(item => Path.GetFileName(item), StringComparer.Ordinal))
        {
            state.CancellationToken.ThrowIfCancellationRequested();
            if (state.VisitedEntries >= state.Options.MaxVisitedEntries)
            {
                state.EntryBudgetReached = true;
                break;
            }

            state.VisitedEntries++;

            string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
            if (!ContractValidator.IsNormalizedRelativePath(relative))
            {
                state.Skip("scan.outsideRoot");
                continue;
            }

            try
            {
                FileAttributes attributes = File.GetAttributes(entry);
                bool directoryEntry = attributes.HasFlag(FileAttributes.Directory);
                string name = Path.GetFileName(entry);

                if (IsSensitive(name, directoryEntry) || IsExcluded(relative, state.Options.ExcludePatterns, directoryEntry))
                {
                    state.Skip("scan.excluded");
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    state.Skip("scan.link");
                    continue;
                }

                if (directoryEntry)
                {
                    VisitDirectory(root, entry, state);
                    continue;
                }

                if (!IsIncluded(relative, state.Options.IncludePatterns))
                {
                    state.Skip("scan.notIncluded");
                    continue;
                }

                if (!TextExtensions.Contains(Path.GetExtension(name)) && !TextExtensions.Contains(name))
                {
                    state.Skip("scan.binary");
                    continue;
                }

                // Explicitly reject no-read permission bits even when the scanning process is privileged.
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(entry) &
                    (UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0)
                {
                    state.Skip("scan.accessDenied");
                    continue;
                }

                long size = new FileInfo(entry).Length;
                if (size > state.Options.MaxBytesPerFile)
                {
                    state.Skip("scan.fileTooLarge");
                    continue;
                }

                if (state.Files.Count >= state.Options.MaxFiles)
                {
                    state.Skip("scan.fileLimit");
                    continue;
                }

                if (size > state.Options.MaxTotalBytes - state.TotalBytes)
                {
                    state.Skip("scan.totalBytesLimit");
                    continue;
                }

                // Read only a bounded prefix for binary detection, never source bodies in the snapshot.
                // Recheck attributes after open to detect common concurrent link replacement.
                using (FileStream stream = new(entry, FileMode.Open, FileAccess.Read, FileShare.Read, BinaryProbeBytes, FileOptions.SequentialScan))
                {
                    if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                    {
                        state.Skip("scan.link");
                        continue;
                    }

                    byte[] prefix = new byte[BinaryProbeBytes];
                    int count = stream.Read(prefix, 0, (int)Math.Min(prefix.Length, size));
                    if (prefix.AsSpan(0, count).Contains((byte)0))
                    {
                        state.Skip("scan.binary");
                        continue;
                    }

                    if (stream.Length != size)
                    {
                        state.Skip("scan.changed");
                        continue;
                    }
                }

                state.Files.Add(new RepositoryFile(relative, size, null));
                state.TotalBytes += size;
            }
            catch (Exception exception) when (IsExpectedFileSystemFailure(exception))
            {
                // Never include exception.Message or an untrusted filename in diagnostics.
                state.Skip(exception is UnauthorizedAccessException or SecurityException ? "scan.accessDenied" : "scan.unavailable");
            }
        }
    }

    private static bool IsSensitive(string name, bool isDirectory)
    {
        if (isDirectory && ExcludedDirectories.Contains(name))
        {
            return true;
        }

        string lower = name.ToLowerInvariant();
        return lower == ".env"
            || lower.StartsWith(".env.", StringComparison.Ordinal)
            || lower is ".npmrc" or ".pypirc" or ".netrc" or "id_rsa" or "id_ed25519"
            || lower.Contains("secret", StringComparison.Ordinal)
            || lower.Contains("credential", StringComparison.Ordinal)
            || lower.Contains("token", StringComparison.Ordinal)
            || lower.StartsWith("appsettings.", StringComparison.Ordinal)
            || lower.StartsWith("private_key", StringComparison.Ordinal)
            || lower.StartsWith("appsettings.local.", StringComparison.Ordinal)
            || lower.Contains(".secrets.", StringComparison.Ordinal)
            || lower.Contains(".credentials.", StringComparison.Ordinal)
            || lower.EndsWith(".key", StringComparison.Ordinal)
            || lower.EndsWith(".pem", StringComparison.Ordinal)
            || lower.EndsWith(".p12", StringComparison.Ordinal)
            || lower.EndsWith(".pfx", StringComparison.Ordinal)
            || lower.EndsWith(".keystore", StringComparison.Ordinal);
    }

    private static bool IsIncluded(string relative, ImmutableArray<string> patterns) =>
        patterns.IsEmpty || patterns.Any(pattern => Matches(relative, pattern));

    private static bool IsExcluded(string relative, ImmutableArray<string> patterns, bool isDirectory) =>
        patterns.Any(pattern => Matches(relative, pattern) || (isDirectory && Matches(relative + "/", pattern)));

    private static bool Matches(string relative, string pattern)
    {
        // Globs are bounded, anchored and timeout-limited; ** spans paths, * and ? do not.
        string regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$";
        string value = pattern.Contains('/') ? relative : Path.GetFileName(relative);
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    private static void ValidateOptions(RepositoryScanOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootPath);
        if (!Path.IsPathFullyQualified(options.RootPath)
            || options.RootPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("RootPath must be an absolute path without traversal segments.", nameof(options));
        }

        if (options.MaxFiles <= 0 || options.MaxBytesPerFile <= 0 || options.MaxTotalBytes <= 0
            || options.MaxVisitedEntries <= 0 || options.MaxFiles > options.MaxVisitedEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "All limits must be positive and MaxFiles must not exceed MaxVisitedEntries.");
        }

        ValidatePatterns(options.IncludePatterns, nameof(options.IncludePatterns));
        ValidatePatterns(options.ExcludePatterns, nameof(options.ExcludePatterns));
    }

    private static void ValidatePatterns(ImmutableArray<string> patterns, string parameter)
    {
        if (patterns.IsDefault || patterns.Length > MaxPatterns)
        {
            throw new ArgumentException("Patterns must be initialized and bounded.", parameter);
        }

        foreach (string pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaxPatternLength
                || pattern.StartsWith('/') || pattern.Contains('\\') || pattern.Contains(':')
                || pattern.Any(char.IsControl) || pattern.Split('/').Any(segment => segment is "." or ".." or ""))
            {
                throw new ArgumentException("Glob patterns must be bounded, relative, and free of traversal.", parameter);
            }
        }
    }

    private static bool IsExpectedFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or NotSupportedException;

    private sealed class ScanState(RepositoryScanOptions options, CancellationToken cancellationToken)
    {
        public RepositoryScanOptions Options { get; } = options;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public List<RepositoryFile> Files { get; } = [];

        public Dictionary<string, int> Omissions { get; } = new(StringComparer.Ordinal);

        public int VisitedEntries
        {
            get;
            set;
        }

        public bool EntryBudgetReached
        {
            get;
            set;
        }

        public long TotalBytes
        {
            get;
            set;
        }

        public void Skip(string code)
        {
            Omissions.TryGetValue(code, out int count);
            Omissions[code] = count + 1;
        }
    }
}
