using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Repo2C4.Core.Acquisition;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.Inspection;

namespace Repo2C4.Mcp;

internal sealed record McpRemoteInspectionResult(
    string SnapshotId,
    string RepositoryId,
    string SchemaVersion,
    DateTimeOffset ExpiresAtUtc,
    int FileCount,
    int EvidenceCount,
    int DiagnosticCount,
    RemoteRepositoryProvenance Acquisition);

internal sealed class McpRemoteRepositoryTools(McpSnapshotStore snapshotStore)
{
    internal void AddTools(McpServerPrimitiveCollection<McpServerTool> tools)
    {
        MethodInfo method = typeof(McpRemoteRepositoryTools).GetMethod(nameof(InspectRemoteRepository))
            ?? throw new InvalidOperationException("Remote MCP tool method was not found.");
        tools.Add(McpServerTool.Create(method, this, new McpServerToolCreateOptions
        {
            Name = "inspect_remote_repository",
            Description = "Acquire one public HTTPS Git repository in an isolated temporary workspace, inspect it with the same bounded evidence pipeline, then delete the workspace. Optional gitRef selects an explicit branch/tag/ref. Private/authenticated repositories, submodules and symbolic links are rejected. Acquisition URL/ref/commit are provenance only, not architectural evidence.",
            ReadOnly = true,
            Destructive = false,
            Idempotent = true,
            OpenWorld = true,
            UseStructuredContent = true,
            SerializerOptions = McpToolJson.Options,
        }));
    }

    [Description("Acquire and inspect a public HTTPS Git repository without executing repository code.")]
    public async Task<McpRemoteInspectionResult> InspectRemoteRepository(
        [Description("Public HTTPS Git repository URL. Embedded credentials, SSH and file URLs are rejected.")]
        string repositoryUrl,
        [Description("Optional Git ref such as refs/heads/main or a tag.")]
        string? gitRef = null,
        [Description("Maximum inventoried files for the evidence scan. Valid range: 1 to 1000.")]
        int maxFiles = McpLimits.MaxFilesPerInspection,
        CancellationToken cancellationToken = default)
    {
        if (maxFiles is < 1 or > McpLimits.MaxFilesPerInspection)
        {
            throw new McpException(
                $"max_files_invalid: maxFiles must be between 1 and {McpLimits.MaxFilesPerInspection}.");
        }

        using CancellationTokenSource timeout = new(McpLimits.ToolExecutionTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);

        try
        {
            RemoteRepositoryAcquirer acquirer = new();
            await using RemoteRepositoryWorkspace workspace = await acquirer.AcquireAsync(
                new RemoteRepositoryRequest(
                    repositoryUrl,
                    gitRef,
                    McpLimits.ToolExecutionTimeout),
                linked.Token).ConfigureAwait(false);

            RepositoryScanOptions options = new(
                workspace.RootPath,
                CreateRepositoryId(workspace.Provenance))
            {
                MaxFiles = maxFiles,
            };
            RepositorySnapshot snapshot = RepositoryFactExtractor.Extract(options, linked.Token);
            McpSnapshotStore.SnapshotEntry entry = snapshotStore.Store(snapshot);
            return McpResponseGuard.EnsureWithinLimit(new McpRemoteInspectionResult(
                entry.SnapshotId,
                snapshot.RepositoryId,
                snapshot.SchemaVersion,
                entry.ExpiresAtUtc,
                snapshot.Files.Length,
                snapshot.Evidence.Length,
                snapshot.Diagnostics.Length,
                workspace.Provenance));
        }
        catch (RemoteRepositoryException exception)
        {
            throw new McpException(exception.Code + ": " + exception.Message);
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new McpException(
                $"tool_timeout: remote acquisition exceeded {McpLimits.ToolExecutionTimeout.TotalSeconds:0} seconds.");
        }
        catch (IOException)
        {
            throw new McpException("repository_unavailable: remote repository could not be inspected safely.");
        }
        catch (ArgumentException)
        {
            throw new McpException("repository_path_invalid: acquired repository could not be inspected safely.");
        }
    }

    private static string CreateRepositoryId(RemoteRepositoryProvenance provenance)
    {
        string identity = provenance.Url.ToLowerInvariant() + "\n" + provenance.Commit;
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return "repo_" + Convert.ToHexString(digest).ToLowerInvariant()[..24];
    }
}
