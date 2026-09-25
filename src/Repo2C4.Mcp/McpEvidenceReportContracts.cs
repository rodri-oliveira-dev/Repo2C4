namespace Repo2C4.Mcp;

public sealed record McpEvidenceReportResult(
    string SnapshotId,
    string SchemaVersion,
    string FileName,
    string Content,
    int ConfirmedAssertions,
    int ReviewRequiredAssertions,
    int ScanWarnings,
    int MissingOrigins);
