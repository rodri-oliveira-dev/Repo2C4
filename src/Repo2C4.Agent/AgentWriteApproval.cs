using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Repo2C4.Agent;

public sealed record AgentWriteFilePreview(string FileName, int Utf8Bytes);

public sealed record AgentWriteChangePreview(
    string FileName,
    string Kind,
    string? PreviousHash,
    string NewHash);

public sealed record AgentWritePlanItem(
    string Level,
    string SnapshotId,
    JsonElement Model,
    string DestinationPath,
    string? C3ContainerId,
    IReadOnlyList<AgentWriteFilePreview> Files,
    IReadOnlyList<AgentWriteChangePreview> Changes,
    IReadOnlyList<string> RequiresReviewIds,
    string PreviewFingerprint,
    bool HasConflicts);

public sealed record AgentWriteApprovalPlan(
    string DestinationRoot,
    IReadOnlyList<AgentWritePlanItem> Items)
{
    public bool HasConflicts => Items.Any(item => item.HasConflicts);
}

public enum AgentWriteApplyStatus
{
    Applied,
    StalePreview,
    Conflict,
    Cancelled,
    Failed,
}

public sealed record AgentWriteApplyResult(
    AgentWriteApplyStatus Status,
    IReadOnlyList<string> Destinations,
    string? Diagnostic);

public interface IAgentMcpWriteGateway
{
    ValueTask<AgentWriteApprovalPlan> PrepareAsync(
        IReadOnlyList<AgentArchitectureProposal> proposals,
        string destinationRoot,
        CancellationToken cancellationToken);

    ValueTask<AgentWriteApplyResult> ApplyAsync(
        AgentWriteApprovalPlan plan,
        CancellationToken cancellationToken);
}

internal sealed class Repo2C4McpWriteGateway(
    McpClientTool generateLikeC4,
    AgentExecutionContext execution)
    : IAgentMcpWriteGateway
{
    private readonly McpClientTool generateLikeC4 =
        generateLikeC4 ?? throw new ArgumentNullException(nameof(generateLikeC4));
    private readonly AgentExecutionContext execution =
        execution ?? throw new ArgumentNullException(nameof(execution));

    public async ValueTask<AgentWriteApprovalPlan> PrepareAsync(
        IReadOnlyList<AgentArchitectureProposal> proposals,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        AgentArchitectureProposal[] writable =
        [
            .. proposals
                .Where(proposal => proposal.Level is "C1" or "C2")
                .OrderBy(proposal => proposal.Level, StringComparer.Ordinal),
        ];

        List<AgentWritePlanItem> items = [];
        foreach (AgentArchitectureProposal proposal in writable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = CombineDestination(destinationRoot, proposal.Level);
            items.Add(
                await PreviewAsync(
                    proposal,
                    destination,
                    cancellationToken).ConfigureAwait(false));
        }

        return new AgentWriteApprovalPlan(destinationRoot, items);
    }

    public async ValueTask<AgentWriteApplyResult> ApplyAsync(
        AgentWriteApprovalPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.Items.Count == 0)
        {
            return new AgentWriteApplyResult(
                AgentWriteApplyStatus.Failed,
                [],
                "No validated C1/C2 proposal is available for writing.");
        }

        foreach (AgentWritePlanItem approved in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AgentArchitectureProposal proposal = new(
                approved.Level,
                approved.SnapshotId,
                approved.Model,
                approved.C3ContainerId);

            AgentWritePlanItem current = await PreviewAsync(
                proposal,
                approved.DestinationPath,
                cancellationToken).ConfigureAwait(false);

            if (current.HasConflicts)
            {
                return new AgentWriteApplyResult(
                    AgentWriteApplyStatus.Conflict,
                    [],
                    "Managed output conflict detected after approval; no write was started.");
            }

            if (!string.Equals(
                    current.PreviewFingerprint,
                    approved.PreviewFingerprint,
                    StringComparison.Ordinal))
            {
                return new AgentWriteApplyResult(
                    AgentWriteApplyStatus.StalePreview,
                    [],
                    "The approved preview is stale; a new preview and approval are required.");
            }
        }

        List<string> written = [];
        foreach (AgentWritePlanItem approved in plan.Items)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new AgentWriteApplyResult(
                    AgentWriteApplyStatus.Cancelled,
                    written,
                    PartialWriteDiagnostic(
                        written,
                        "The approved LikeC4 write was cancelled."));
            }

            try
            {
                CallToolResult result = await CallGenerateLikeC4Async(
                    new Dictionary<string, object?>
                    {
                        ["snapshotId"] = approved.SnapshotId,
                        ["model"] = approved.Model,
                        ["dryRun"] = false,
                        ["write"] = true,
                        ["destinationPath"] = approved.DestinationPath,
                        ["c3ContainerId"] = approved.C3ContainerId,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (result.IsError is true)
                {
                    return ContainsErrorCode(result, "managed_output_conflict")
                        ? new AgentWriteApplyResult(
                            AgentWriteApplyStatus.Conflict,
                            written,
                            PartialWriteDiagnostic(
                                written,
                                "Managed output conflict prevented the approved write."))
                        : new AgentWriteApplyResult(
                            AgentWriteApplyStatus.Failed,
                            written,
                            PartialWriteDiagnostic(
                                written,
                                "The approved LikeC4 write failed."));
                }

                if (result.StructuredContent is not JsonElement content
                    || !content.TryGetProperty("written", out JsonElement writtenElement)
                    || writtenElement.ValueKind != JsonValueKind.True
                    || !content.TryGetProperty("dryRun", out JsonElement dryRunElement)
                    || dryRunElement.ValueKind != JsonValueKind.False)
                {
                    return new AgentWriteApplyResult(
                        AgentWriteApplyStatus.Failed,
                        written,
                        PartialWriteDiagnostic(
                            written,
                            "The MCP write returned an unexpected result."));
                }

                written.Add(approved.DestinationPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new AgentWriteApplyResult(
                    AgentWriteApplyStatus.Cancelled,
                    written,
                    PartialWriteDiagnostic(
                        written,
                        "The approved LikeC4 write was cancelled."));
            }
#pragma warning disable CA1031 // Raw MCP errors may contain local paths; return only controlled classifications.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                return exception.Message.Contains(
                    "managed_output_conflict",
                    StringComparison.Ordinal)
                    ? new AgentWriteApplyResult(
                        AgentWriteApplyStatus.Conflict,
                        written,
                        PartialWriteDiagnostic(
                            written,
                            "Managed output conflict prevented the approved write."))
                    : new AgentWriteApplyResult(
                        AgentWriteApplyStatus.Failed,
                        written,
                        PartialWriteDiagnostic(
                            written,
                            "The approved LikeC4 write failed."));
            }
        }

        return new AgentWriteApplyResult(
            AgentWriteApplyStatus.Applied,
            written,
            null);
    }

    private static string PartialWriteDiagnostic(
        IReadOnlyList<string> written,
        string diagnostic) =>
        written.Count == 0
            ? diagnostic
            : diagnostic
                + " One or more approved destinations were already applied; "
                + "the result lists them explicitly. Re-run preview and validation before retrying.";

    private async ValueTask<CallToolResult> CallGenerateLikeC4Async(
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        using AgentOperationScope operation = execution.BeginTool("generate_likec4");
        try
        {
            CallToolResult result = await generateLikeC4
                .CallAsync(arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            execution.ObserveToolResponse(result.StructuredContent);
            operation.Succeed();
            return result;
        }
        catch (AgentBudgetExceededException exception)
        {
            operation.Fail(exception.Code);
            throw;
        }
        catch (OperationCanceledException)
        {
            operation.Cancel();
            throw;
        }
#pragma warning disable CA1031 // Only a controlled error code is recorded before rethrowing.
        catch (Exception)
#pragma warning restore CA1031
        {
            operation.Fail("tool_failed");
            throw;
        }
    }

    private async ValueTask<AgentWritePlanItem> PreviewAsync(
        AgentArchitectureProposal proposal,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        CallToolResult result = await CallGenerateLikeC4Async(
            new Dictionary<string, object?>
            {
                ["snapshotId"] = proposal.SnapshotId,
                ["model"] = proposal.Model,
                ["dryRun"] = true,
                ["write"] = false,
                ["destinationPath"] = destinationPath,
                ["c3ContainerId"] = proposal.C3ContainerId,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.IsError is true || result.StructuredContent is not JsonElement content)
        {
            throw new InvalidOperationException("LikeC4 write preview could not be prepared.");
        }

        AgentWriteFilePreview[] files =
        [
            .. content.GetProperty("files")
                .EnumerateArray()
                .Select(item => new AgentWriteFilePreview(
                    item.GetProperty("fileName").GetString() ?? string.Empty,
                    item.GetProperty("utf8Bytes").GetInt32()))
                .OrderBy(item => item.FileName, StringComparer.Ordinal),
        ];

        AgentWriteChangePreview[] changes =
        [
            .. content.GetProperty("changes")
                .EnumerateArray()
                .Select(item => new AgentWriteChangePreview(
                    item.GetProperty("fileName").GetString() ?? string.Empty,
                    item.GetProperty("kind").GetString() ?? string.Empty,
                    item.TryGetProperty("previousHash", out JsonElement previousHash)
                        && previousHash.ValueKind == JsonValueKind.String
                            ? previousHash.GetString()
                            : null,
                    item.GetProperty("newHash").GetString() ?? string.Empty))
                .OrderBy(item => item.FileName, StringComparer.Ordinal),
        ];

        bool hasConflicts =
            content.TryGetProperty("hasConflicts", out JsonElement conflicts)
            && conflicts.ValueKind == JsonValueKind.True;

        string fingerprint = ComputeFingerprint(
            proposal,
            destinationPath,
            content);

        return new AgentWritePlanItem(
            proposal.Level,
            proposal.SnapshotId,
            proposal.Model.Clone(),
            destinationPath,
            proposal.C3ContainerId,
            files,
            changes,
            GetRequiresReviewIds(proposal.Model),
            fingerprint,
            hasConflicts);
    }

    private static string CombineDestination(string root, string level) =>
        root.TrimEnd('/') + "/" + level.ToLowerInvariant();

    private static IReadOnlyList<string> GetRequiresReviewIds(JsonElement model)
    {
        List<string> ids = [];
        AddReviewIds(model, "elements", ids);
        AddReviewIds(model, "relations", ids);
        return [.. ids.Order(StringComparer.Ordinal)];
    }

    private static void AddReviewIds(
        JsonElement model,
        string propertyName,
        List<string> ids)
    {
        if (!model.TryGetProperty(propertyName, out JsonElement assertions)
            || assertions.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement assertion in assertions.EnumerateArray())
        {
            if (assertion.TryGetProperty("status", out JsonElement status)
                && string.Equals(
                    status.GetString(),
                    "requiresReview",
                    StringComparison.Ordinal)
                && assertion.TryGetProperty("id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is string value)
            {
                ids.Add(value);
            }
        }
    }

    private static string ComputeFingerprint(
        AgentArchitectureProposal proposal,
        string destinationPath,
        JsonElement preview)
    {
        string payload =
            proposal.SnapshotId
            + "\n"
            + proposal.Level
            + "\n"
            + (proposal.C3ContainerId ?? string.Empty)
            + "\n"
            + destinationPath
            + "\n"
            + proposal.Model.GetRawText()
            + "\n"
            + preview.GetRawText();

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    private static bool ContainsErrorCode(
        CallToolResult result,
        string code)
    {
        string serialized = JsonSerializer.Serialize(result.Content);
        return serialized.Contains(code, StringComparison.Ordinal);
    }
}

public enum HumanApprovalDecision
{
    Approve,
    Deny,
    Cancel,
}

public interface IWriteApprovalPrompt
{
    ValueTask<HumanApprovalDecision> RequestAsync(
        AgentWriteApprovalPlan plan,
        ToolApprovalRequestContent request,
        CancellationToken cancellationToken);
}

public sealed class ConsoleWriteApprovalPrompt(
    TextReader input,
    TextWriter output) : IWriteApprovalPrompt
{
    private readonly TextReader input =
        input ?? throw new ArgumentNullException(nameof(input));
    private readonly TextWriter output =
        output ?? throw new ArgumentNullException(nameof(output));

    public async ValueTask<HumanApprovalDecision> RequestAsync(
        AgentWriteApprovalPlan plan,
        ToolApprovalRequestContent request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(request);

        await output.WriteLineAsync("LikeC4 write approval required.").ConfigureAwait(false);
        await output.WriteLineAsync(
            "Destination root: " + plan.DestinationRoot).ConfigureAwait(false);

        foreach (AgentWritePlanItem item in plan.Items)
        {
            await output.WriteLineAsync(
                item.Level + " -> " + item.DestinationPath).ConfigureAwait(false);

            foreach (AgentWriteChangePreview change in item.Changes)
            {
                await output.WriteLineAsync(
                    "  change: " + change.Kind + " " + change.FileName).ConfigureAwait(false);
            }

            foreach (AgentWriteFilePreview file in item.Files)
            {
                await output.WriteLineAsync(
                    "  file: " + file.FileName + " (" + file.Utf8Bytes + " bytes)").ConfigureAwait(false);
            }

            if (item.RequiresReviewIds.Count > 0)
            {
                await output.WriteLineAsync(
                    "  requiresReview: "
                    + string.Join(", ", item.RequiresReviewIds)).ConfigureAwait(false);
            }
        }

        await output.WriteLineAsync(
            "Approval request id: " + request.RequestId).ConfigureAwait(false);
        await output.WriteAsync(
            "Approve this exact validated preview? [y/N/cancel]: ").ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        string? answer = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (answer is null
            || string.Equals(answer.Trim(), "cancel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(answer.Trim(), "c", StringComparison.OrdinalIgnoreCase))
        {
            return HumanApprovalDecision.Cancel;
        }

        return answer.Trim() is "y" or "Y"
            || string.Equals(answer.Trim(), "yes", StringComparison.OrdinalIgnoreCase)
            ? HumanApprovalDecision.Approve
            : HumanApprovalDecision.Deny;
    }
}

public enum WriteApprovalStatus
{
    Applied,
    Denied,
    Cancelled,
    StalePreview,
    Conflict,
    Failed,
}

public sealed record WriteApprovalResult(
    WriteApprovalStatus Status,
    IReadOnlyList<string> Destinations,
    string? Diagnostic)
{
    public string ToDisplayText()
    {
        string status = Status switch
        {
            WriteApprovalStatus.Applied => "applied",
            WriteApprovalStatus.Denied => "denied",
            WriteApprovalStatus.Cancelled => "cancelled",
            WriteApprovalStatus.StalePreview => "stale_preview",
            WriteApprovalStatus.Conflict => "conflict",
            _ => "failed",
        };

        StringBuilder builder = new();
        builder.Append("Write status: ");
        builder.AppendLine(status);

        foreach (string destination in Destinations)
        {
            builder.Append("Written: ");
            builder.AppendLine(destination);
        }

        if (!string.IsNullOrWhiteSpace(Diagnostic))
        {
            builder.Append("Write diagnostic: ");
            builder.AppendLine(Diagnostic);
        }

        return builder.ToString().TrimEnd();
    }
}

public interface IWriteApprovalRunner
{
    Task<WriteApprovalResult> RunAsync(
        IChatClient chatClient,
        AgentHostOptions options,
        IAgentMcpSession mcpSession,
        ArchitectureWorkflowResult workflowResult,
        CancellationToken cancellationToken);
}

public sealed class WriteApprovalRunner(IWriteApprovalPrompt prompt)
    : IWriteApprovalRunner
{
    private const string ApplyToolName = "apply_validated_likec4_preview";

    private readonly IWriteApprovalPrompt prompt =
        prompt ?? throw new ArgumentNullException(nameof(prompt));

    public async Task<WriteApprovalResult> RunAsync(
        IChatClient chatClient,
        AgentHostOptions options,
        IAgentMcpSession mcpSession,
        ArchitectureWorkflowResult workflowResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mcpSession);
        ArgumentNullException.ThrowIfNull(workflowResult);

        if (string.IsNullOrWhiteSpace(options.WriteDestination))
        {
            return new WriteApprovalResult(
                WriteApprovalStatus.Denied,
                [],
                "No write destination was explicitly selected.");
        }

        if (workflowResult.Status is not (
            ArchitectureWorkflowStatus.Completed
            or ArchitectureWorkflowStatus.RequiresReview))
        {
            return new WriteApprovalResult(
                WriteApprovalStatus.Denied,
                [],
                "Writing is unavailable because the architecture workflow did not validate successfully.");
        }

        IReadOnlyList<AgentArchitectureProposal> proposals =
            mcpSession.InvocationState.SnapshotProposals();

        AgentWriteApprovalPlan plan;
        try
        {
            plan = await mcpSession.WriteGateway
                .PrepareAsync(
                    proposals,
                    options.WriteDestination,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WriteApprovalResult(
                WriteApprovalStatus.Cancelled,
                [],
                "Approval preparation was cancelled.");
        }
#pragma warning disable CA1031 // Preview errors are deliberately reduced to a safe local diagnostic.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new WriteApprovalResult(
                WriteApprovalStatus.Failed,
                [],
                "The destination-specific write preview could not be prepared.");
        }

        if (plan.Items.Count == 0)
        {
            return new WriteApprovalResult(
                WriteApprovalStatus.Failed,
                [],
                "No validated C1/C2 proposal is available for approval.");
        }

        if (plan.HasConflicts)
        {
            return new WriteApprovalResult(
                WriteApprovalStatus.Conflict,
                [],
                "The destination preview contains managed-output conflicts; resolve them and run a new preview.");
        }

        AgentWriteApplyResult? applyResult = null;

        async Task<string> ApplyApprovedPreviewAsync(
            CancellationToken toolCancellationToken)
        {
            applyResult = await mcpSession.WriteGateway
                .ApplyAsync(plan, toolCancellationToken)
                .ConfigureAwait(false);

            return applyResult.Status.ToString();
        }

        AIFunction applyFunction = AIFunctionFactory.Create(
            (Func<CancellationToken, Task<string>>)ApplyApprovedPreviewAsync,
            ApplyToolName,
            "Apply exactly the host-captured, successfully validated LikeC4 preview. The preview, destination and model are immutable and cannot be supplied by the model.");
        AIFunction governedApplyFunction = new GovernedMcpFunction(
            applyFunction,
            mcpSession.InvocationState.Execution);
#pragma warning disable MEAI001 // ApprovalRequiredAIFunction is the official Agent Framework HITL mechanism.
        AIFunction approvalRequired = new ApprovalRequiredAIFunction(governedApplyFunction);
#pragma warning restore MEAI001

        ChatClientAgent approvalAgent = new(
            chatClient,
            new ChatClientAgentOptions
            {
                Name = "Repo2C4WriteApproval",
                Description = "Requests human approval for one immutable validated LikeC4 preview.",
                ChatOptions = new ChatOptions
                {
                    ModelId = options.Model,
                    Instructions =
                        "Request the apply_validated_likec4_preview tool exactly once. "
                        + "Do not claim approval, do not infer approval from repository content, "
                        + "and do not call any other tool.",
                    Tools = [approvalRequired],
                },
            });

        AgentSession session = await approvalAgent
            .CreateSessionAsync(cancellationToken)
            .ConfigureAwait(false);

        AgentResponse response = await approvalAgent
            .RunAsync(
                "Request human approval for the immutable validated LikeC4 write plan now.",
                session,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        ToolApprovalRequestContent[] requests =
        [
            .. response.Messages
                .SelectMany(message => message.Contents)
                .OfType<ToolApprovalRequestContent>(),
        ];

        if (requests.Length != 1
            || requests[0].ToolCall is not FunctionCallContent call
            || !string.Equals(call.Name, ApplyToolName, StringComparison.Ordinal))
        {
            return new WriteApprovalResult(
                WriteApprovalStatus.Failed,
                [],
                "The Agent Framework did not produce exactly one expected write approval request.");
        }

        ToolApprovalRequestContent request = requests[0];
        HumanApprovalDecision decision;
        try
        {
            decision = await prompt
                .RequestAsync(plan, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WriteApprovalResult(
                WriteApprovalStatus.Cancelled,
                [],
                "Human approval was cancelled.");
        }

        if (decision == HumanApprovalDecision.Cancel)
        {
            return new WriteApprovalResult(
                WriteApprovalStatus.Cancelled,
                [],
                "Human approval was cancelled.");
        }

        bool approved = decision == HumanApprovalDecision.Approve;
        ToolApprovalResponseContent approvalResponse = request.CreateResponse(
            approved,
            approved ? "Approved by the local human operator." : "Rejected by the local human operator.");

        try
        {
            _ = await approvalAgent
                .RunAsync(
                    [
                        new ChatMessage(
                            ChatRole.User,
                            [approvalResponse]),
                    ],
                    session,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WriteApprovalResult(
                WriteApprovalStatus.Cancelled,
                applyResult?.Destinations ?? [],
                applyResult?.Diagnostic ?? "The approved write was cancelled.");
        }
#pragma warning disable CA1031 // If protected apply completed, preserve its controlled outcome instead of replacing it with a provider/framework failure.
        catch (Exception) when (applyResult is not null)
#pragma warning restore CA1031
        {
            return ToWriteApprovalResult(applyResult);
        }

        if (!approved)
        {
            return new WriteApprovalResult(
                WriteApprovalStatus.Denied,
                [],
                "The human operator rejected the write request.");
        }

        if (applyResult is null)
        {
            return new WriteApprovalResult(
                WriteApprovalStatus.Failed,
                [],
                "Approval was granted but the protected write tool did not execute.");
        }

        return ToWriteApprovalResult(applyResult);
    }

    private static WriteApprovalResult ToWriteApprovalResult(
        AgentWriteApplyResult applyResult) =>
        applyResult.Status switch
        {
            AgentWriteApplyStatus.Applied => new WriteApprovalResult(
                WriteApprovalStatus.Applied,
                applyResult.Destinations,
                null),
            AgentWriteApplyStatus.StalePreview => new WriteApprovalResult(
                WriteApprovalStatus.StalePreview,
                applyResult.Destinations,
                applyResult.Diagnostic),
            AgentWriteApplyStatus.Conflict => new WriteApprovalResult(
                WriteApprovalStatus.Conflict,
                applyResult.Destinations,
                applyResult.Diagnostic),
            AgentWriteApplyStatus.Cancelled => new WriteApprovalResult(
                WriteApprovalStatus.Cancelled,
                applyResult.Destinations,
                applyResult.Diagnostic),
            _ => new WriteApprovalResult(
                WriteApprovalStatus.Failed,
                applyResult.Destinations,
                applyResult.Diagnostic),
        };
}
