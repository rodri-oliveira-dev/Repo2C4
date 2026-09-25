namespace Repo2C4.Mcp;

public sealed record McpEvidenceReportResult(
    string SnapshotId,
    string SchemaVersion,
    string ReportFileName,
    int ConfirmedAssertions,
    int ReviewRequiredAssertions,
    int ScanWarnings,
    int MissingOrigins,
    string[] ReviewRequiredIds,
    string[] WarningCodes);
