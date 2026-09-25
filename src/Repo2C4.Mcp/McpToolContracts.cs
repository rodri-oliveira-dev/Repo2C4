using System.Text.Json;
using System.Text.Json.Serialization;
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

public sealed class CamelCaseGeneratedChangeKindConverter : JsonConverter<GeneratedFileChangeKind>
{
    public override GeneratedFileChangeKind Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        string? name = reader.GetString();
        if (Enum.TryParse(name, ignoreCase: true, out GeneratedFileChangeKind kind) &&
            Enum.IsDefined(kind))
        {
            return kind;
        }

        throw new JsonException("Unknown managed output change kind.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        GeneratedFileChangeKind value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }
}

public sealed record McpGeneratedFileChange(
    string FileName,
    [property: JsonConverter(typeof(CamelCaseGeneratedChangeKindConverter))]
    GeneratedFileChangeKind Kind,
    string? PreviousHash,
    string NewHash);

public sealed record McpGenerateLikeC4Result(
    string SnapshotId,
    string SchemaVersion,
    bool DryRun,
    bool Written,
    string? DestinationPath,
    McpLikeC4File[] Files,
    McpGeneratedFileChange[] Changes,
    bool HasConflicts);

public sealed record McpValidateLikeC4Result(
    string SnapshotId,
    string SchemaVersion,
    string Workspace,
    bool IsValid,
    int ExitCode,
    bool TimedOut,
    Repo2C4.Core.LikeC4.LikeC4ValidationDiagnostic[] Diagnostics);
