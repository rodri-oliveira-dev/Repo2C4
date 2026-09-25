namespace Repo2C4.Mcp;

internal static class McpLimits
{
    internal static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ToolExecutionTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan SnapshotLifetime = TimeSpan.FromMinutes(30);
    internal const int MaxFilesPerInspection = 1_000;
    internal const int MaxPageSize = 100;
    internal const int DefaultPageSize = 50;
    internal const int MaxSummaryItems = 20;
    internal const int MaxResponseBytes = 1_048_576;
    internal const int ProtocolEnvelopeReserveBytes = 4_096;
}
