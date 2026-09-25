namespace Repo2C4.Mcp;

/// <summary>
/// Enforces the local filesystem boundary for paths accepted by the MCP host.
/// </summary>
public static class RepositoryAccessPolicy
{
    /// <summary>Validates that a repository root is absolute, existing and not a symbolic/reparse link.</summary>
    public static string ValidateAuthorizedRoot(string rootPath) =>
        GetAuthorizedRoot(rootPath);

    /// <summary>
    /// Resolves one existing relative path without permitting traversal or linked path components.
    /// </summary>
    public static string ResolveExistingPath(
        string authorizedRoot,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string root = GetAuthorizedRoot(authorizedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath) || HasParentTraversal(relativePath))
        {
            throw new UnauthorizedAccessException("Requested path must remain relative to the authorized repository root.");
        }

        string platformRelativePath = relativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(root, platformRelativePath));

        if (!IsWithinRoot(root, candidate))
        {
            throw new UnauthorizedAccessException("Requested path resolves outside the authorized repository root.");
        }

        string normalizedRelative = Path.GetRelativePath(root, candidate);
        if (string.Equals(normalizedRelative, ".", StringComparison.Ordinal))
        {
            return root;
        }

        string current = root;
        foreach (string segment in normalizedRelative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = Path.Combine(current, segment);

            if (!Directory.Exists(current) && !File.Exists(current))
            {
                throw new DirectoryNotFoundException("Requested repository path does not exist.");
            }

            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException("Linked repository paths are not allowed.");
            }
        }

        return candidate;
    }

    /// <summary>
    /// Resolves a relative destination directory that may not exist yet while validating every existing path component.
    /// </summary>
    public static string ResolveWritableDirectoryPath(
        string authorizedRoot,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string root = GetAuthorizedRoot(authorizedRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath) || HasParentTraversal(relativePath))
        {
            throw new UnauthorizedAccessException("Destination must remain relative to the authorized repository root.");
        }

        string platformRelativePath = relativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(root, platformRelativePath));
        if (!IsWithinRoot(root, candidate))
        {
            throw new UnauthorizedAccessException("Destination resolves outside the authorized repository root.");
        }

        string normalizedRelative = Path.GetRelativePath(root, candidate);
        if (string.Equals(normalizedRelative, ".", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Destination must be an explicit child directory.");
        }

        string current = root;
        foreach (string segment in normalizedRelative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = Path.Combine(current, segment);

            if (File.Exists(current) && !Directory.Exists(current))
            {
                throw new IOException("Destination path contains an existing file.");
            }

            if (!Directory.Exists(current))
            {
                continue;
            }

            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException("Linked destination paths are not allowed.");
            }
        }

        return candidate;
    }

    private static string GetAuthorizedRoot(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        if (!Path.IsPathFullyQualified(rootPath) || HasParentTraversal(rootPath))
        {
            throw new ArgumentException("Authorized repository root must be an absolute path without parent traversal.", nameof(rootPath));
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!Directory.Exists(root))
        {
            throw new ArgumentException("Authorized repository root must exist.", nameof(rootPath));
        }

        FileAttributes attributes = File.GetAttributes(root);
        if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ArgumentException("Authorized repository root must be a real, non-linked directory.", nameof(rootPath));
        }

        return root;
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(root, candidate, comparison))
        {
            return true;
        }

        string rootPrefix = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootPrefix, comparison);
    }

    private static bool HasParentTraversal(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => string.Equals(segment, "..", StringComparison.Ordinal));
}
