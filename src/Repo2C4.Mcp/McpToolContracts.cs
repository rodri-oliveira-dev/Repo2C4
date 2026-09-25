using Repo2C4.Core.Contracts;
using Repo2C4.Core.Generation;

namespace Repo2C4.Mcp;

public sealed record McpEvidenceCategoryCount(string Category, int Count);

public sealed record McpInspectRepositoryResult(
    string SnapshotId,
    string RepositoryId,
    string SchemaVersion,
    DateTimeOffset ExpiresAtUtc,
    int FileCount,
    int EvidenceCount,
    int DiagnosticCount,
    McpEvidenceCategoryCount[] EvidenceCategories,
    Evidence[] Facts,
    bool FactsTruncated,
    RepositoryDiagnostic[] Diagnostics,
    bool DiagnosticsTruncated);

public sealed record McpEvidencePageResult(
    string SnapshotId,
    string RepositoryId,
    string SchemaVersion,
    DateTimeOffset ExpiresAtUtc,
    int TotalMatched,
    Evidence[] Items,
    string? NextCursor);

public sealed record McpSnapshotPageResult(
    string SnapshotId,
    string RepositoryId,
    string SchemaVersion,
    DateTimeOffset ExpiresAtUtc,
    string Section,
    int TotalMatched,
    RepositoryFile[] Files,
    RepositoryDiagnostic[] Diagnostics,
    string? NextCursor);

public sealed record McpLikeC4File(
    string FileName,
    string Content,
    int Utf8Bytes);

public sealed record McpGenerateLikeC4Result(
    string SnapshotId,
    string SchemaVersion,
    bool DryRun,
    bool Written,
    string? DestinationPath,
    McpLikeC4File[] Files,
    GeneratedFileChange[] Changes,
    bool HasConflicts);

public sealed record McpValidateLikeC4Result(
    string SnapshotId,
    string SchemaVersion,
    string Workspace,
    bool IsValid,
    int ExitCode,
    bool TimedOut,
    Repo2C4.Core.LikeC4.LikeC4ValidationDiagnostic[] Diagnostics);
