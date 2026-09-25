namespace Repo2C4.Mcp;

internal static class McpLimits
{
    internal static readonly TimeSpan InitializationTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ToolExecutionTimeout = TimeSpan.FromSeconds(30);
    internal const int MaxFilesPerInspection = 1_000;
    internal const int MaxResponseBytes = 1_048_576;
}
