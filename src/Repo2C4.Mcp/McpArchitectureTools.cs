using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.Inspection;

namespace Repo2C4.Mcp;

internal sealed class McpArchitectureTools
{
    private readonly string _authorizedRoot;
    private readonly McpSnapshotStore _snapshotStore;

    internal McpArchitectureTools(string authorizedRoot, McpSnapshotStore snapshotStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizedRoot);
        ArgumentNullException.ThrowIfNull(snapshotStore);
        _authorizedRoot = authorizedRoot;
        _snapshotStore = snapshotStore;
    }

    internal McpServerPrimitiveCollection<McpServerTool> CreateToolCollection()
    {
        McpServerPrimitiveCollection<McpServerTool> tools = new(StringComparer.Ordinal);
        tools.Add(CreateTool(
            nameof(InspectRepository),
            "inspect_repository",
            "Inspect one repository directory within the authorized local root and return a bounded evidence summary. " +
            "The tool returns metadata and static v1 evidence only, never source-file contents. " +
            $"repositoryPath is relative to the authorized root; maxFiles must be 1..{McpLimits.MaxFilesPerInspection}. " +
            $"Snapshots expire after {McpLimits.SnapshotLifetime.TotalMinutes:0} minutes and are scoped to this stdio session. " +
            "Errors are controlled codes such as repository_path_invalid, tool_timeout and response_limit_exceeded."));
        tools.Add(CreateTool(
            nameof(GetEvidence),
            "get_evidence",
            "Return one bounded page of v1 evidence from a snapshot created by inspect_repository. " +
            "Optional category is an exact evidence category and pathPrefix is a repository-relative prefix. " +
            $"pageSize defaults to {McpLimits.DefaultPageSize} and cannot exceed {McpLimits.MaxPageSize}. " +
            "Use nextCursor unchanged with the same filters. Errors include snapshot_not_found_or_expired and cursor_invalid."));
        tools.Add(CreateTool(
            nameof(GetSnapshot),
            "get_snapshot",
            "Return bounded snapshot metadata pages without repository file contents. " +
            "section must be 'files' or 'diagnostics'; pathPrefix optionally filters repository-relative metadata. " +
            $"pageSize defaults to {McpLimits.DefaultPageSize} and cannot exceed {McpLimits.MaxPageSize}. " +
            "Use nextCursor unchanged with the same section and filter. Errors include snapshot_not_found_or_expired and cursor_invalid."));
        return tools;
    }

    [Description("Inspect an authorized local repository and create a session-scoped evidence snapshot.")]
    public McpInspectRepositoryResult InspectRepository(
        [Description("Repository directory relative to the authorized MCP root. Use '.' for the authorized root.")]
        string repositoryPath = ".",
        [Description("Maximum inventoried files for this call. Valid range: 1 to 1000.")]
        int maxFiles = McpLimits.MaxFilesPerInspection,
        CancellationToken cancellationToken = default)
    {
        if (maxFiles is < 1 or > McpLimits.MaxFilesPerInspection)
        {
            throw new McpException(
                $"max_files_invalid: maxFiles must be between 1 and {McpLimits.MaxFilesPerInspection}.");
        }

        using CancellationTokenSource timeout = new(McpLimits.ToolExecutionTimeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            string repositoryRoot = ResolveRepositoryDirectory(repositoryPath, linked.Token);
            RepositoryScanOptions options = new(repositoryRoot, CreateRepositoryId(repositoryRoot))
            {
                MaxFiles = maxFiles,
            };
            RepositorySnapshot snapshot = RepositoryFactExtractor.Extract(options, linked.Token);
            McpSnapshotStore.SnapshotEntry entry = _snapshotStore.Store(snapshot);

            McpEvidenceCategoryCount[] categories =
            [
                .. snapshot.Evidence
                    .GroupBy(item => item.Category, StringComparer.Ordinal)
                    .OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new McpEvidenceCategoryCount(group.Key, group.Count())),
            ];
            Evidence[] facts = [.. snapshot.Evidence.Take(McpLimits.MaxSummaryItems)];
            RepositoryDiagnostic[] diagnostics = [.. snapshot.Diagnostics.Take(McpLimits.MaxSummaryItems)];

            return McpResponseGuard.EnsureWithinLimit(new McpInspectRepositoryResult(
                entry.SnapshotId,
                snapshot.RepositoryId,
                snapshot.SchemaVersion,
                entry.ExpiresAtUtc,
                snapshot.Files.Length,
                snapshot.Evidence.Length,
                snapshot.Diagnostics.Length,
                categories,
                facts,
                snapshot.Evidence.Length > facts.Length,
                diagnostics,
                snapshot.Diagnostics.Length > diagnostics.Length));
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new McpException(
                $"tool_timeout: repository inspection exceeded {McpLimits.ToolExecutionTimeout.TotalSeconds:0} seconds.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new McpException("repository_path_invalid: requested repository path is not authorized.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new McpException("repository_path_invalid: requested repository directory does not exist.");
        }
        catch (ArgumentException)
        {
            throw new McpException("repository_path_invalid: requested repository path is invalid.");
        }
        catch (IOException)
        {
            throw new McpException("repository_unavailable: repository could not be inspected safely.");
        }
    }

    [Description("Retrieve a filtered, paginated page of evidence from a session-scoped snapshot.")]
    public McpEvidencePageResult GetEvidence(
        [Description("Snapshot ID returned by inspect_repository.")]
        string snapshotId,
        [Description("Optional exact evidence category, for example dotnet.runtime.http.candidate.")]
        string? category = null,
        [Description("Optional repository-relative path prefix. It filters metadata only and never reads the file.")]
        string? pathPrefix = null,
        [Description("Maximum items in this page. Valid range: 1 to 100.")]
        int pageSize = McpLimits.DefaultPageSize,
        [Description("Opaque nextCursor returned by the previous page. Do not construct or modify it.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePageSize(pageSize);
        string? normalizedCategory = NormalizeCategory(category);
        string? normalizedPathPrefix = NormalizePathPrefix(pathPrefix);
        McpSnapshotStore.SnapshotEntry entry = _snapshotStore.Get(snapshotId);

        string filterKey = (normalizedCategory ?? string.Empty) + "\n" + (normalizedPathPrefix ?? string.Empty);
        int offset = _snapshotStore.ReadCursor(cursor, "evidence", snapshotId, filterKey);

        IEnumerable<Evidence> query = entry.Snapshot.Evidence;
        if (normalizedCategory is not null)
        {
            query = query.Where(item => string.Equals(item.Category, normalizedCategory, StringComparison.Ordinal));
        }

        if (normalizedPathPrefix is not null)
        {
            query = query.Where(item => IsPathWithinPrefix(item.RelativePath, normalizedPathPrefix));
        }

        Evidence[] filtered = [.. query];
        if (offset > filtered.Length)
        {
            throw new McpException("cursor_invalid: cursor offset is outside the filtered evidence set.");
        }

        Evidence[] items = [.. filtered.Skip(offset).Take(pageSize)];
        int nextOffset = offset + items.Length;
        string? nextCursor = nextOffset < filtered.Length
            ? _snapshotStore.CreateCursor("evidence", snapshotId, filterKey, nextOffset)
            : null;

        return McpResponseGuard.EnsureWithinLimit(new McpEvidencePageResult(
            entry.SnapshotId,
            entry.Snapshot.RepositoryId,
            entry.Snapshot.SchemaVersion,
            entry.ExpiresAtUtc,
            filtered.Length,
            items,
            nextCursor));
    }

    [Description("Retrieve paginated snapshot file metadata or diagnostics without reading repository contents.")]
    public McpSnapshotPageResult GetSnapshot(
        [Description("Snapshot ID returned by inspect_repository.")]
        string snapshotId,
        [Description("Snapshot section to return: 'files' or 'diagnostics'.")]
        string section = "files",
        [Description("Optional repository-relative path prefix used only as a metadata filter.")]
        string? pathPrefix = null,
        [Description("Maximum items in this page. Valid range: 1 to 100.")]
        int pageSize = McpLimits.DefaultPageSize,
        [Description("Opaque nextCursor returned by the previous page. Do not construct or modify it.")]
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePageSize(pageSize);
        string normalizedSection = NormalizeSection(section);
        string? normalizedPathPrefix = NormalizePathPrefix(pathPrefix);
        McpSnapshotStore.SnapshotEntry entry = _snapshotStore.Get(snapshotId);

        string filterKey = normalizedSection + "\n" + (normalizedPathPrefix ?? string.Empty);
        int offset = _snapshotStore.ReadCursor(cursor, "snapshot", snapshotId, filterKey);

        if (normalizedSection == "files")
        {
            IEnumerable<RepositoryFile> query = entry.Snapshot.Files;
            if (normalizedPathPrefix is not null)
            {
                query = query.Where(item => IsPathWithinPrefix(item.RelativePath, normalizedPathPrefix));
            }

            RepositoryFile[] filtered = [.. query];
            if (offset > filtered.Length)
            {
                throw new McpException("cursor_invalid: cursor offset is outside the filtered snapshot set.");
            }

            RepositoryFile[] items = [.. filtered.Skip(offset).Take(pageSize)];
            int nextOffset = offset + items.Length;
            string? nextCursor = nextOffset < filtered.Length
                ? _snapshotStore.CreateCursor("snapshot", snapshotId, filterKey, nextOffset)
                : null;

            return McpResponseGuard.EnsureWithinLimit(new McpSnapshotPageResult(
                entry.SnapshotId,
                entry.Snapshot.RepositoryId,
                entry.Snapshot.SchemaVersion,
                entry.ExpiresAtUtc,
                normalizedSection,
                filtered.Length,
                items,
                [],
                nextCursor));
        }
        else
        {
            IEnumerable<RepositoryDiagnostic> query = entry.Snapshot.Diagnostics;
            if (normalizedPathPrefix is not null)
            {
                query = query.Where(item =>
                    item.RelativePath is not null &&
                    IsPathWithinPrefix(item.RelativePath, normalizedPathPrefix));
            }

            RepositoryDiagnostic[] filtered = [.. query];
            if (offset > filtered.Length)
            {
                throw new McpException("cursor_invalid: cursor offset is outside the filtered snapshot set.");
            }

            RepositoryDiagnostic[] items = [.. filtered.Skip(offset).Take(pageSize)];
            int nextOffset = offset + items.Length;
            string? nextCursor = nextOffset < filtered.Length
                ? _snapshotStore.CreateCursor("snapshot", snapshotId, filterKey, nextOffset)
                : null;

            return McpResponseGuard.EnsureWithinLimit(new McpSnapshotPageResult(
                entry.SnapshotId,
                entry.Snapshot.RepositoryId,
                entry.Snapshot.SchemaVersion,
                entry.ExpiresAtUtc,
                normalizedSection,
                filtered.Length,
                [],
                items,
                nextCursor));
        }
    }

    private McpServerTool CreateTool(string methodName, string toolName, string description)
    {
        MethodInfo method = typeof(McpArchitectureTools).GetMethod(methodName)
            ?? throw new InvalidOperationException("MCP tool method was not found.");

        return McpServerTool.Create(method, this, new McpServerToolCreateOptions
        {
            Name = toolName,
            Description = description,
            ReadOnly = true,
            Destructive = false,
            Idempotent = true,
            OpenWorld = false,
            UseStructuredContent = true,
            SerializerOptions = McpToolJson.Options,
        });
    }

    private string ResolveRepositoryDirectory(string repositoryPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || repositoryPath.Length > 1_024)
        {
            throw new ArgumentException("Repository path is invalid.", nameof(repositoryPath));
        }

        string repositoryRoot =
            RepositoryAccessPolicy.ResolveExistingPath(_authorizedRoot, repositoryPath, cancellationToken);
        if (!Directory.Exists(repositoryRoot))
        {
            throw new DirectoryNotFoundException();
        }

        return repositoryRoot;
    }

    private static void ValidatePageSize(int pageSize)
    {
        if (pageSize is < 1 or > McpLimits.MaxPageSize)
        {
            throw new McpException(
                $"page_size_invalid: pageSize must be between 1 and {McpLimits.MaxPageSize}.");
        }
    }

    private static string NormalizeSection(string section)
    {
        if (string.Equals(section, "files", StringComparison.OrdinalIgnoreCase))
        {
            return "files";
        }

        if (string.Equals(section, "diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            return "diagnostics";
        }

        throw new McpException("section_invalid: section must be 'files' or 'diagnostics'.");
    }

    private static string? NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            return null;
        }

        string normalized = category.Trim();
        if (normalized.Length > 128 || normalized.Any(char.IsControl))
        {
            throw new McpException("category_invalid: category filter is invalid.");
        }

        return normalized;
    }

    private static string? NormalizePathPrefix(string? pathPrefix)
    {
        if (string.IsNullOrWhiteSpace(pathPrefix))
        {
            return null;
        }

        string normalized = pathPrefix.Trim().Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length > 1_024 ||
            Path.IsPathRooted(pathPrefix) ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment => segment is "." or ".."))
        {
            throw new McpException("path_filter_invalid: pathPrefix must be a repository-relative metadata prefix.");
        }

        return normalized;
    }

    private static bool IsPathWithinPrefix(string relativePath, string pathPrefix) =>
        string.Equals(relativePath, pathPrefix, StringComparison.Ordinal) ||
        relativePath.StartsWith(pathPrefix + "/", StringComparison.Ordinal);

    private string CreateRepositoryId(string fullRepositoryPath)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(fullRepositoryPath);
        string name = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "repository";
        }

        string relative = Path.GetRelativePath(_authorizedRoot, trimmed).Replace('\\', '/');
        string normalizedRelative = NormalizeRepositoryIdentityPath(
            relative,
            caseInsensitive: OperatingSystem.IsWindows());
        string identity = string.Equals(normalizedRelative, ".", StringComparison.Ordinal)
            ? name.ToLowerInvariant()
            : name.ToLowerInvariant() + "\n" + normalizedRelative;
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "repo_" + Convert.ToHexString(digest).ToLowerInvariant()[..24];
    }

    internal static string NormalizeRepositoryIdentityPath(
        string relativePath,
        bool caseInsensitive) =>
        caseInsensitive ? relativePath.ToLowerInvariant() : relativePath;
}
