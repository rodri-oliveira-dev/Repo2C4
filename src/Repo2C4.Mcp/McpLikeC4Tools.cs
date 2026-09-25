using System.ComponentModel;
using System.Reflection;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Repo2C4.Core.Contracts;
using Repo2C4.Core.C3;
using Repo2C4.Core.LikeC4;
using Repo2C4.Core.Review;

namespace Repo2C4.Mcp;

internal sealed class McpLikeC4Tools
{
    private readonly string _authorizedRoot;
    private readonly McpSnapshotStore _snapshotStore;

    internal McpLikeC4Tools(string authorizedRoot, McpSnapshotStore snapshotStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizedRoot);
        ArgumentNullException.ThrowIfNull(snapshotStore);
        _authorizedRoot = authorizedRoot;
        _snapshotStore = snapshotStore;
    }

    internal void AddTools(McpServerPrimitiveCollection<McpServerTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        tools.Add(CreateTool(
            nameof(GenerateLikeC4),
            "generate_likec4",
            "Generate deterministic LikeC4 from a v1 ArchitectureModel bound to a snapshot in this MCP session. " +
            "dryRun defaults to true and performs no writes. Writing requires dryRun=false, write=true and an explicit " +
            "repository-relative destinationPath inside the authorized root; existing generated files are never overwritten. " +
            "The model snapshot must exactly match snapshotId and unsupported static/candidate evidence cannot be promoted " +
            "to a confirmed container boundary or runtime relation.",
            readOnly: false,
            idempotent: false));

        tools.Add(CreateTool(
            nameof(GetEvidenceReport),
            "get_evidence_report",
            "Return a bounded summary of the deterministic evidence-report.md for a session-bound v1 ArchitectureModel. " +
            "The embedded snapshot must exactly match snapshotId. The response contains counts plus bounded review-required IDs " +
            "and warning codes without source bodies, configuration values, absolute paths or repository writes.",
            readOnly: true,
            idempotent: true));

        tools.Add(CreateTool(
            nameof(ValidateLikeC4),
            "validate_likec4",
            "Validate deterministic LikeC4 with the controlled official LikeC4 CLI adapter. " +
            "When destinationPath is omitted, the supplied v1 ArchitectureModel is emitted into an isolated temporary workspace " +
            "and validated without repository writes. When destinationPath is supplied, it must be an existing directory inside " +
            "the authorized root. Validation failures are returned as structured bounded diagnostics.",
            readOnly: true,
            idempotent: true));
    }

    [Description("Preview or explicitly write deterministic LikeC4 generated from a session-bound v1 model.")]
    public async Task<McpGenerateLikeC4Result> GenerateLikeC4(
        [Description("Snapshot ID returned by inspect_repository in this stdio session.")]
        string snapshotId,
        [Description("Complete ArchitectureModel v1. Its embedded snapshot must exactly match snapshotId.")]
        ArchitectureModel model,
        [Description("Safe default. true returns the proposed files without writing anything.")]
        bool dryRun = true,
        [Description("Explicit write authorization. Writing also requires dryRun=false and destinationPath.")]
        bool write = false,
        [Description("Repository-relative destination directory inside the authorized root. Required only for writing.")]
        string? destinationPath = null,
        [Description("Optional C2 container ID. When supplied, only that container receives an additional C3 component view.")]
        string? c3ContainerId = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        McpSnapshotStore.SnapshotEntry entry = ValidateModelBinding(snapshotId, model);

        if (write && dryRun)
        {
            throw new McpException(
                "write_not_authorized: set dryRun=false together with write=true to authorize filesystem changes.");
        }

        if (!dryRun && !write)
        {
            throw new McpException(
                "write_not_authorized: dryRun=false requires explicit write=true authorization.");
        }

        if (write && string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new McpException(
                "destination_required: writing requires an explicit repository-relative destinationPath.");
        }

        List<LikeC4GeneratedFile> generated = [.. LikeC4Emitter.Emit(model)];
        if (!string.IsNullOrWhiteSpace(c3ContainerId))
        {
            ArchitectureC3Model c3 = ArchitectureC3Builder.Build(model, c3ContainerId);
            generated.AddRange(LikeC4Emitter.EmitC3(c3));
        }
        McpLikeC4File[] files =
        [
            .. generated.Select(file => new McpLikeC4File(
                file.FileName,
                file.Content,
                Encoding.UTF8.GetByteCount(file.Content))),
        ];

        if (!write)
        {
            return McpResponseGuard.EnsureWithinLimit(new McpGenerateLikeC4Result(
                entry.SnapshotId,
                model.SchemaVersion,
                true,
                false,
                NormalizeDestinationForResponse(destinationPath),
                files));
        }

        string outputRoot;
        try
        {
            outputRoot = RepositoryAccessPolicy.ResolveWritableDirectoryPath(
                _authorizedRoot,
                destinationPath!,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or PathTooLongException)
        {
            throw new McpException(
                "destination_invalid: destinationPath must be a safe child directory inside the authorized root.");
        }

        await WriteGeneratedFilesAsync(outputRoot, generated, cancellationToken).ConfigureAwait(false);

        return McpResponseGuard.EnsureWithinLimit(new McpGenerateLikeC4Result(
            entry.SnapshotId,
            model.SchemaVersion,
            false,
            true,
            NormalizeDestinationForResponse(destinationPath),
            files));
    }

    [Description("Return a metadata-only evidence provenance report for a session-bound v1 model.")]
    public McpEvidenceReportResult GetEvidenceReport(
        [Description("Snapshot ID returned by inspect_repository in this stdio session.")]
        string snapshotId,
        [Description("Complete ArchitectureModel v1. Its embedded snapshot must exactly match snapshotId.")]
        ArchitectureModel model,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        McpSnapshotStore.SnapshotEntry entry = ValidateModelBinding(snapshotId, model);
        EvidenceReportResult report = EvidenceReportGenerator.Generate(model);

        string[] reviewRequiredIds =
        [
            .. model.Elements
                .Where(item => item.Status == ReviewStatus.RequiresReview)
                .Select(item => item.Id)
                .Concat(model.Relations
                    .Where(item => item.Status == ReviewStatus.RequiresReview)
                    .Select(item => item.Id))
                .OrderBy(item => item, StringComparer.Ordinal)
                .Take(McpLimits.MaxSummaryItems),
        ];
        string[] warningCodes =
        [
            .. model.Snapshot.Diagnostics
                .Where(item => item.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error)
                .Select(item => item.Code)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .Take(McpLimits.MaxSummaryItems),
        ];

        return McpResponseGuard.EnsureWithinLimit(new McpEvidenceReportResult(
            entry.SnapshotId,
            model.SchemaVersion,
            report.FileName,
            report.Summary.ConfirmedAssertions,
            report.Summary.ReviewRequiredAssertions,
            report.Summary.ScanWarnings,
            report.Summary.MissingOrigins,
            reviewRequiredIds,
            warningCodes));
    }

    [Description("Validate proposed or previously written LikeC4 using the controlled official LikeC4 CLI adapter.")]
    public async Task<McpValidateLikeC4Result> ValidateLikeC4(
        [Description("Snapshot ID returned by inspect_repository in this stdio session.")]
        string snapshotId,
        [Description("Complete ArchitectureModel v1 bound to snapshotId.")]
        ArchitectureModel model,
        [Description("Optional existing repository-relative LikeC4 directory. Omit to validate the proposed model in a temporary workspace.")]
        string? destinationPath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        McpSnapshotStore.SnapshotEntry entry = ValidateModelBinding(snapshotId, model);

        string workspace;
        string workspaceLabel;
        string? temporaryWorkspace = null;

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            IReadOnlyList<LikeC4GeneratedFile> generated = LikeC4Emitter.Emit(model);
            temporaryWorkspace = Directory.CreateTempSubdirectory("repo2c4-mcp-likec4-").FullName;
            workspace = temporaryWorkspace;
            workspaceLabel = "proposed";

            try
            {
                await WriteTemporaryFilesAsync(workspace, generated, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDeleteDirectory(temporaryWorkspace);
                throw;
            }
        }
        else
        {
            try
            {
                workspace = RepositoryAccessPolicy.ResolveExistingPath(
                    _authorizedRoot,
                    destinationPath,
                    cancellationToken);
                if (!Directory.Exists(workspace))
                {
                    throw new DirectoryNotFoundException();
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or DirectoryNotFoundException
                    or IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or PathTooLongException)
            {
                throw new McpException(
                    "validation_path_invalid: destinationPath must be an existing safe directory inside the authorized root.");
            }

            workspaceLabel = NormalizeDestinationForResponse(destinationPath)!;
        }

        try
        {
            LikeC4ValidationResult result = await LikeC4CliValidator.ValidateAsync(
                workspace,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return McpResponseGuard.EnsureWithinLimit(new McpValidateLikeC4Result(
                entry.SnapshotId,
                model.SchemaVersion,
                workspaceLabel,
                result.IsValid,
                result.ExitCode,
                result.TimedOut,
                [.. result.Diagnostics]));
        }
        finally
        {
            if (temporaryWorkspace is not null)
            {
                TryDeleteDirectory(temporaryWorkspace);
            }
        }
    }

    private McpSnapshotStore.SnapshotEntry ValidateModelBinding(
        string snapshotId,
        ArchitectureModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        McpSnapshotStore.SnapshotEntry entry = _snapshotStore.Get(snapshotId);

        IReadOnlyList<ContractError> modelErrors = ContractValidator.ValidateModel(model);
        if (modelErrors.Count > 0)
        {
            string safeErrors = string.Join(
                ", ",
                modelErrors.Take(8).Select(error => error.Code + " at " + error.Path));
            throw new McpException("model_invalid: " + safeErrors);
        }

        string expectedSnapshot;
        string suppliedSnapshot;
        try
        {
            expectedSnapshot = ContractJson.SerializeSnapshot(entry.Snapshot);
            suppliedSnapshot = ContractJson.SerializeSnapshot(model.Snapshot);
        }
        catch (ContractValidationException exception)
        {
            string safeErrors = string.Join(
                ", ",
                exception.Errors.Take(8).Select(error => error.Code + " at " + error.Path));
            throw new McpException("model_invalid: " + safeErrors);
        }

        if (!string.Equals(expectedSnapshot, suppliedSnapshot, StringComparison.Ordinal))
        {
            throw new McpException(
                "snapshot_mismatch: the ArchitectureModel snapshot must exactly match snapshotId from this MCP session.");
        }

        EnsureReviewBoundaries(model, entry.Snapshot);
        return entry;
    }

    private static void EnsureReviewBoundaries(
        ArchitectureModel model,
        RepositorySnapshot snapshot)
    {
        Dictionary<string, Evidence> evidence = snapshot.Evidence.ToDictionary(
            item => item.Id,
            StringComparer.Ordinal);

        foreach (ArchitectureElement element in model.Elements)
        {
            if (element.Kind != ArchitectureElementKind.Container ||
                element.Status != ReviewStatus.Confirmed ||
                element.EvidenceIds.IsEmpty)
            {
                continue;
            }

            if (element.EvidenceIds
                .Select(id => evidence[id])
                .All(IsStaticRepositorySignal))
            {
                throw new McpException(
                    "model_review_required: repository-static evidence cannot confirm a C2 deployment/container boundary.");
            }
        }

        foreach (ArchitectureRelation relation in model.Relations)
        {
            if (relation.Status != ReviewStatus.Confirmed || relation.EvidenceIds.IsEmpty)
            {
                continue;
            }

            if (relation.EvidenceIds
                .Select(id => evidence[id])
                .All(IsStaticRepositorySignal))
            {
                throw new McpException(
                    "model_review_required: repository-static or candidate evidence cannot confirm a runtime relation.");
            }
        }
    }

    private static bool IsStaticRepositorySignal(Evidence evidence) =>
        evidence.Category.StartsWith("dotnet.", StringComparison.Ordinal) ||
        evidence.Category.StartsWith("deployment.", StringComparison.Ordinal);

    private async Task WriteGeneratedFilesAsync(
        string outputRoot,
        IReadOnlyList<LikeC4GeneratedFile> files,
        CancellationToken cancellationToken)
    {
        List<string> createdFiles = [];
        List<string> createdDirectories = [];

        try
        {
            CreateDestinationDirectories(outputRoot, createdDirectories, cancellationToken);

            foreach (LikeC4GeneratedFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = ResolveGeneratedTarget(outputRoot, file.FileName);
                if (File.Exists(target) || Directory.Exists(target))
                {
                    throw new McpException(
                        "destination_exists: generated LikeC4 files are never overwritten.");
                }
            }

            _ = RepositoryAccessPolicy.ResolveExistingPath(
                _authorizedRoot,
                Path.GetRelativePath(_authorizedRoot, outputRoot),
                cancellationToken);

            foreach (LikeC4GeneratedFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = ResolveGeneratedTarget(outputRoot, file.FileName);
                FileStream stream = new(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    useAsync: true);
                await using (stream.ConfigureAwait(false))
                {
                    createdFiles.Add(target);
                    StreamWriter writer = new(
                        stream,
                        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                        bufferSize: 1_024,
                        leaveOpen: true);
                    await using (writer.ConfigureAwait(false))
                    {
                        await writer.WriteAsync(file.Content.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (McpException)
        {
            CleanupPartialWrite(createdFiles, createdDirectories);
            throw;
        }
        catch (OperationCanceledException)
        {
            CleanupPartialWrite(createdFiles, createdDirectories);
            throw;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or PathTooLongException)
        {
            CleanupPartialWrite(createdFiles, createdDirectories);
            throw new McpException(
                "write_failed: LikeC4 files could not be created safely without overwrite.");
        }
    }

    private void CreateDestinationDirectories(
        string outputRoot,
        ICollection<string> createdDirectories,
        CancellationToken cancellationToken)
    {
        string relative = Path.GetRelativePath(_authorizedRoot, outputRoot);
        string current = _authorizedRoot;

        foreach (string segment in relative.Split(
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
                Directory.CreateDirectory(current);
                createdDirectories.Add(current);
            }

            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnauthorizedAccessException("Linked destination paths are not allowed.");
            }

            _ = RepositoryAccessPolicy.ResolveExistingPath(
                _authorizedRoot,
                Path.GetRelativePath(_authorizedRoot, current),
                cancellationToken);
        }
    }

    private static async Task WriteTemporaryFilesAsync(
        string workspace,
        IReadOnlyList<LikeC4GeneratedFile> files,
        CancellationToken cancellationToken)
    {
        foreach (LikeC4GeneratedFile file in files)
        {
            string target = ResolveGeneratedTarget(workspace, file.FileName);
            await File.WriteAllTextAsync(
                target,
                file.Content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ResolveGeneratedTarget(string outputRoot, string fileName)
    {
        string target = Path.GetFullPath(Path.Combine(outputRoot, fileName));
        string? parent = Path.GetDirectoryName(target);
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!string.Equals(parent, outputRoot, comparison))
        {
            throw new McpException("generated_path_invalid: generated file escaped the selected workspace.");
        }

        return target;
    }

    private static void CleanupPartialWrite(
        IReadOnlyList<string> createdFiles,
        IReadOnlyList<string> createdDirectories)
    {
        foreach (string path in createdFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
            {
                // Best-effort rollback after a failed protected write.
            }
        }

        foreach (string directory in createdDirectories.Reverse())
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException)
            {
                // Best-effort rollback of directories created by this call.
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException)
        {
            // Temporary validation workspaces contain generated LikeC4 only; cleanup is best effort.
        }
    }

    private static string? NormalizeDestinationForResponse(string? destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return null;
        }

        return destinationPath.Trim().Replace('\\', '/').Trim('/');
    }

    private McpServerTool CreateTool(
        string methodName,
        string toolName,
        string description,
        bool readOnly,
        bool idempotent)
    {
        MethodInfo method = typeof(McpLikeC4Tools).GetMethod(methodName)
            ?? throw new InvalidOperationException("MCP LikeC4 tool method was not found.");

        return McpServerTool.Create(method, this, new McpServerToolCreateOptions
        {
            Name = toolName,
            Description = description,
            ReadOnly = readOnly,
            Destructive = false,
            Idempotent = idempotent,
            OpenWorld = false,
            UseStructuredContent = true,
            SerializerOptions = McpToolJson.Options,
        });
    }
}
