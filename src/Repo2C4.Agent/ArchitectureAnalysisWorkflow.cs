using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Repo2C4.Agent;

public enum ArchitectureWorkflowStatus
{
    Completed,
    RequiresReview,
    ValidationFailed,
    InsufficientEvidence,
    Cancelled,
    Failed,
}

public sealed record ArchitectureWorkflowResult(
    ArchitectureWorkflowStatus Status,
    int ValidationAttempts,
    string Summary,
    IReadOnlyList<string> Diagnostics)
{
    public string ToDisplayText()
    {
        StringBuilder builder = new();
        builder.Append("Status: ");
        builder.AppendLine(ToStatusText(Status));
        builder.Append("Validation attempts: ");
        builder.AppendLine(ValidationAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(Summary))
        {
            builder.AppendLine();
            builder.AppendLine(Summary.Trim());
        }

        if (Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Workflow diagnostics");
            foreach (string diagnostic in Diagnostics)
            {
                builder.Append("- ");
                builder.AppendLine(diagnostic);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string ToStatusText(ArchitectureWorkflowStatus status) =>
        status switch
        {
            ArchitectureWorkflowStatus.Completed => "completed",
            ArchitectureWorkflowStatus.RequiresReview => "requires_review",
            ArchitectureWorkflowStatus.ValidationFailed => "validation_failed",
            ArchitectureWorkflowStatus.InsufficientEvidence => "insufficient_evidence",
            ArchitectureWorkflowStatus.Cancelled => "cancelled",
            _ => "failed",
        };
}

public sealed record ArchitectureWorkflowState(
    int Attempt,
    IReadOnlyList<AgentArchitectureProposal> Proposals,
    string AnalysisSummary,
    IReadOnlyList<string> EvidenceReports,
    IReadOnlyList<string> PreviewSummaries,
    bool? ValidationSucceeded,
    IReadOnlyList<string> ValidationDiagnostics,
    ArchitectureWorkflowStatus? TerminalStatus)
{
    public static ArchitectureWorkflowState Initial() =>
        new(
            1,
            [],
            string.Empty,
            [],
            [],
            null,
            [],
            null);

    public bool ShouldRetry =>
        TerminalStatus is null
        && ValidationSucceeded is false;

    public bool ShouldFinalize =>
        TerminalStatus is not null
        || ValidationSucceeded is true;
}

public interface IArchitectureWorkflowOperations
{
    ValueTask<ArchitectureWorkflowState> AnalyzeAsync(
        ArchitectureWorkflowState state,
        CancellationToken cancellationToken);

    ValueTask<ArchitectureWorkflowState> GetEvidenceReportAsync(
        ArchitectureWorkflowState state,
        CancellationToken cancellationToken);

    ValueTask<ArchitectureWorkflowState> PreviewAsync(
        ArchitectureWorkflowState state,
        CancellationToken cancellationToken);

    ValueTask<ArchitectureWorkflowState> ValidateAsync(
        ArchitectureWorkflowState state,
        CancellationToken cancellationToken);

    ValueTask<ArchitectureWorkflowState> CorrectAsync(
        ArchitectureWorkflowState state,
        int attempt,
        CancellationToken cancellationToken);
}

public interface IArchitectureAnalysisWorkflowRunner
{
    Task<ArchitectureWorkflowResult> RunAsync(
        AIAgent agent,
        AgentHostOptions options,
        IAgentMcpSession mcpSession,
        CancellationToken cancellationToken);
}

public sealed class ArchitectureAnalysisWorkflowRunner(
    IAgentSessionRunner sessionRunner) : IArchitectureAnalysisWorkflowRunner
{
    private readonly IAgentSessionRunner sessionRunner =
        sessionRunner ?? throw new ArgumentNullException(nameof(sessionRunner));

    public async Task<ArchitectureWorkflowResult> RunAsync(
        AIAgent agent,
        AgentHostOptions options,
        IAgentMcpSession mcpSession,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(mcpSession);

        AgentArchitectureWorkflowOperations operations = new(
            agent,
            options,
            mcpSession,
            sessionRunner);

        ArchitectureAnalysisWorkflow workflow = new(
            operations,
            options.MaxValidationAttempts);

        return await workflow
            .RunAsync(ArchitectureWorkflowState.Initial(), cancellationToken)
            .ConfigureAwait(false);
    }
}

public sealed class ArchitectureAnalysisWorkflow
{
    private readonly Workflow workflow;

    public ArchitectureAnalysisWorkflow(
        IArchitectureWorkflowOperations operations,
        int maxValidationAttempts)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxValidationAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxValidationAttempts, 3);

        workflow = Build(operations, maxValidationAttempts);
    }

    public Workflow Definition => workflow;

    public async Task<ArchitectureWorkflowResult> RunAsync(
        ArchitectureWorkflowState initialState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialState);

        try
        {
            Run run = await InProcessExecution
                .RunAsync(
                    workflow,
                    initialState,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            await using (run.ConfigureAwait(false))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new ArchitectureWorkflowResult(
                        ArchitectureWorkflowStatus.Cancelled,
                        0,
                        string.Empty,
                        ["Workflow execution was cancelled."]);
                }

                ArchitectureWorkflowResult? result = run.NewEvents
                    .OfType<WorkflowOutputEvent>()
                    .Select(output => output.Data)
                    .OfType<ArchitectureWorkflowResult>()
                    .LastOrDefault();

                return result ?? new ArchitectureWorkflowResult(
                    ArchitectureWorkflowStatus.Failed,
                    0,
                    string.Empty,
                    ["Workflow completed without a structured result."]);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return CancelledResult();
        }
#pragma warning disable CA1031 // Workflow runtime may wrap cancellation; token state is authoritative at this boundary.
        catch (Exception) when (cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return CancelledResult();
        }
#pragma warning disable CA1031 // Workflow boundary converts unexpected framework failures to a controlled result.
        catch (Exception)
#pragma warning restore CA1031
        {
            return new ArchitectureWorkflowResult(
                ArchitectureWorkflowStatus.Failed,
                0,
                string.Empty,
                ["Workflow execution failed unexpectedly."]);
        }
    }

    private static ArchitectureWorkflowResult CancelledResult() =>
        new(
            ArchitectureWorkflowStatus.Cancelled,
            0,
            string.Empty,
            ["Workflow execution was cancelled."]);

    public static Workflow Build(
        IArchitectureWorkflowOperations operations,
        int maxValidationAttempts)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxValidationAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxValidationAttempts, 3);

        AnalysisExecutor analysis = new(operations);
        FinalizeExecutor finalize = new();

        WorkflowBuilder builder = new(analysis);
        Executor previous = analysis;

        for (int attempt = 1; attempt <= maxValidationAttempts; attempt++)
        {
            EvidenceReportExecutor evidenceReport = new(operations, attempt);
            PreviewExecutor preview = new(operations, attempt);
            ValidationExecutor validation = new(operations, attempt);

            builder.AddEdge(previous, evidenceReport);
            builder.AddEdge(evidenceReport, preview);
            builder.AddEdge(preview, validation);

            if (attempt == maxValidationAttempts)
            {
                builder.AddEdge(validation, finalize);
                continue;
            }

            CorrectionExecutor correction = new(operations, attempt + 1);
            builder.AddEdge<ArchitectureWorkflowState>(
                validation,
                finalize,
                condition: state => state?.ShouldFinalize is true);
            builder.AddEdge<ArchitectureWorkflowState>(
                validation,
                correction,
                condition: state => state?.ShouldRetry is true);

            previous = correction;
        }

        builder.WithOutputFrom(finalize);
        return builder.Build();
    }

    private sealed class AnalysisExecutor(IArchitectureWorkflowOperations operations)
        : Executor<ArchitectureWorkflowState, ArchitectureWorkflowState>("analysis")
    {
        private readonly IArchitectureWorkflowOperations operations = operations;

        public override ValueTask<ArchitectureWorkflowState> HandleAsync(
            ArchitectureWorkflowState message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            return operations.AnalyzeAsync(message, cancellationToken);
        }
    }

    private sealed class EvidenceReportExecutor(
        IArchitectureWorkflowOperations operations,
        int attempt)
        : Executor<ArchitectureWorkflowState, ArchitectureWorkflowState>(
            "evidence-report-" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture))
    {
        private readonly IArchitectureWorkflowOperations operations = operations;

        public override ValueTask<ArchitectureWorkflowState> HandleAsync(
            ArchitectureWorkflowState message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            return operations.GetEvidenceReportAsync(message, cancellationToken);
        }
    }

    private sealed class PreviewExecutor(
        IArchitectureWorkflowOperations operations,
        int attempt)
        : Executor<ArchitectureWorkflowState, ArchitectureWorkflowState>(
            "preview-" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture))
    {
        private readonly IArchitectureWorkflowOperations operations = operations;

        public override ValueTask<ArchitectureWorkflowState> HandleAsync(
            ArchitectureWorkflowState message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            return operations.PreviewAsync(message, cancellationToken);
        }
    }

    private sealed class ValidationExecutor(
        IArchitectureWorkflowOperations operations,
        int attempt)
        : Executor<ArchitectureWorkflowState, ArchitectureWorkflowState>(
            "validation-" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture))
    {
        private readonly IArchitectureWorkflowOperations operations = operations;

        public override ValueTask<ArchitectureWorkflowState> HandleAsync(
            ArchitectureWorkflowState message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            return operations.ValidateAsync(message, cancellationToken);
        }
    }

    private sealed class CorrectionExecutor(
        IArchitectureWorkflowOperations operations,
        int attempt)
        : Executor<ArchitectureWorkflowState, ArchitectureWorkflowState>(
            "correction-" + attempt.ToString(System.Globalization.CultureInfo.InvariantCulture))
    {
        private readonly IArchitectureWorkflowOperations operations = operations;
        private readonly int attempt = attempt;

        public override ValueTask<ArchitectureWorkflowState> HandleAsync(
            ArchitectureWorkflowState message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            return operations.CorrectAsync(message, attempt, cancellationToken);
        }
    }

    private sealed class FinalizeExecutor()
        : Executor<ArchitectureWorkflowState, ArchitectureWorkflowResult>("finalize")
    {
        public override ValueTask<ArchitectureWorkflowResult> HandleAsync(
            ArchitectureWorkflowState message,
            IWorkflowContext context,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();

            ArchitectureWorkflowStatus status = ResolveStatus(message);
            ArchitectureWorkflowResult result = new(
                status,
                message.Attempt,
                message.AnalysisSummary,
                message.ValidationDiagnostics);

            return ValueTask.FromResult(result);
        }

        private static ArchitectureWorkflowStatus ResolveStatus(
            ArchitectureWorkflowState state)
        {
            if (state.TerminalStatus is not null)
            {
                return state.TerminalStatus.Value;
            }

            if (state.ValidationSucceeded is false)
            {
                return ArchitectureWorkflowStatus.ValidationFailed;
            }

            bool requiresReview = state.Proposals.Any(ProposalRequiresReview);
            return requiresReview
                ? ArchitectureWorkflowStatus.RequiresReview
                : ArchitectureWorkflowStatus.Completed;
        }

        private static bool ProposalRequiresReview(AgentArchitectureProposal proposal)
        {
            JsonElement model = proposal.Model;
            return ContainsRequiresReview(model, "elements")
                || ContainsRequiresReview(model, "relations");
        }

        private static bool ContainsRequiresReview(
            JsonElement model,
            string propertyName)
        {
            if (!model.TryGetProperty(propertyName, out JsonElement assertions)
                || assertions.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement assertion in assertions.EnumerateArray())
            {
                if (assertion.TryGetProperty("status", out JsonElement status)
                    && string.Equals(
                        status.GetString(),
                        "requiresReview",
                        StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

internal sealed class AgentArchitectureWorkflowOperations(
    AIAgent agent,
    AgentHostOptions options,
    IAgentMcpSession mcpSession,
    IAgentSessionRunner sessionRunner) : IArchitectureWorkflowOperations
{
    private const int MaxDiagnostics = 8;
    private const int MaxDiagnosticLength = 500;

    private readonly AIAgent agent = agent ?? throw new ArgumentNullException(nameof(agent));
    private readonly AgentHostOptions options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly IAgentMcpSession mcpSession =
        mcpSession ?? throw new ArgumentNullException(nameof(mcpSession));
    private readonly IAgentSessionRunner sessionRunner =
        sessionRunner ?? throw new ArgumentNullException(nameof(sessionRunner));

    public ValueTask<ArchitectureWorkflowState> AnalyzeAsync(
        ArchitectureWorkflowState state,
        CancellationToken cancellationToken) =>
        RunProposalAsync(
            state with
            {
                Attempt = 1,
            },
            EvidenceFirstAnalysisPrompt.BuildForWorkflow(options),
            cancellationToken);

    public async ValueTask<ArchitectureWorkflowState> GetEvidenceReportAsync(
        ArchitectureWorkflowState state,
        CancellationToken cancellationToken)
    {
        if (state.TerminalStatus is not null)
        {
            return state;
        }

        AIFunction? tool = GetTool("get_evidence_report");
        if (tool is null)
        {
            return Fail(state, "Evidence-report tool is unavailable.");
        }

        try
        {
            List<string> reports = [];
            foreach (AgentArchitectureProposal proposal in state.Proposals)
            {
                _ = await tool.InvokeAsync(
                    new AIFunctionArguments
                    {
                        ["snapshotId"] = proposal.SnapshotId,
                        ["model"] = proposal.Model,
                    },
                    cancellationToken).ConfigureAwait(false);

                reports.Add(proposal.Level + " evidence report completed.");
            }

            return state with
            {
                EvidenceReports = reports,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // MCP details are intentionally hidden behind a controlled workflow diagnostic.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Fail(state, "Evidence-report stage failed.");
        }
    }

    public async ValueTask<ArchitectureWorkflowState> PreviewAsync(
        ArchitectureWorkflowState state,
        CancellationToken cancellationToken)
    {
        if (state.TerminalStatus is not null)
        {
            return state;
        }

        AIFunction? tool = GetTool("generate_likec4");
        if (tool is null)
        {
            return Fail(state, "LikeC4 preview tool is unavailable.");
        }

        try
        {
            List<string> previews = [];
            foreach (AgentArchitectureProposal proposal in state.Proposals)
            {
                object? result = await tool.InvokeAsync(
                    new AIFunctionArguments
                    {
                        ["snapshotId"] = proposal.SnapshotId,
                        ["model"] = proposal.Model,
                        ["dryRun"] = true,
                        ["write"] = false,
                        ["destinationPath"] = null,
                        ["c3ContainerId"] = proposal.C3ContainerId,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (TryGetStructuredContent(result, out JsonElement content)
                    && content.TryGetProperty("written", out JsonElement written)
                    && written.ValueKind == JsonValueKind.True)
                {
                    return Fail(state, "Safety policy rejected an unexpected write result.");
                }

                previews.Add(proposal.Level + " LikeC4 preview completed.");
            }

            return state with
            {
                PreviewSummaries = previews,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // MCP details are intentionally hidden behind a controlled workflow diagnostic.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Fail(state, "LikeC4 preview stage failed.");
        }
    }

    public async ValueTask<ArchitectureWorkflowState> ValidateAsync(
        ArchitectureWorkflowState state,
        CancellationToken cancellationToken)
    {
        if (state.TerminalStatus is not null)
        {
            return state;
        }

        AIFunction? tool = GetTool("validate_likec4");
        if (tool is null)
        {
            return Fail(state, "LikeC4 validation tool is unavailable.");
        }

        try
        {
            List<string> diagnostics = [];
            bool valid = true;

            foreach (AgentArchitectureProposal proposal in state.Proposals)
            {
                object? result = await tool.InvokeAsync(
                    new AIFunctionArguments
                    {
                        ["snapshotId"] = proposal.SnapshotId,
                        ["model"] = proposal.Model,
                        ["destinationPath"] = null,
                        ["c3ContainerId"] = proposal.C3ContainerId,
                    },
                    cancellationToken).ConfigureAwait(false);

                if (!TryGetStructuredContent(result, out JsonElement content)
                    || !content.TryGetProperty("isValid", out JsonElement isValid)
                    || isValid.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return Fail(state, "LikeC4 validation returned an invalid response.");
                }

                if (!isValid.GetBoolean())
                {
                    valid = false;
                    AddValidationDiagnostics(
                        diagnostics,
                        proposal.Level,
                        content);
                }
            }

            if (!valid && diagnostics.Count == 0)
            {
                diagnostics.Add("LikeC4 validation failed without a detailed diagnostic.");
            }

            return state with
            {
                ValidationSucceeded = valid,
                ValidationDiagnostics = diagnostics,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // MCP details are intentionally hidden behind a controlled workflow diagnostic.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Fail(state, "LikeC4 validation stage failed.");
        }
    }

    public ValueTask<ArchitectureWorkflowState> CorrectAsync(
        ArchitectureWorkflowState state,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (state.TerminalStatus is not null)
        {
            return ValueTask.FromResult(state);
        }

        return RunProposalAsync(
            state with
            {
                Attempt = attempt,
                ValidationSucceeded = null,
                EvidenceReports = [],
                PreviewSummaries = [],
            },
            EvidenceFirstAnalysisPrompt.BuildCorrectionForWorkflow(
                options,
                attempt,
                state.ValidationDiagnostics),
            cancellationToken);
    }

    private async ValueTask<ArchitectureWorkflowState> RunProposalAsync(
        ArchitectureWorkflowState state,
        string prompt,
        CancellationToken cancellationToken)
    {
        mcpSession.InvocationState.BeginAttempt();

        try
        {
            string summary = await sessionRunner
                .RunAsync(agent, prompt, cancellationToken)
                .ConfigureAwait(false);

            IReadOnlyList<AgentArchitectureProposal> proposals =
                mcpSession.InvocationState.SnapshotProposals();

            if (proposals.Count == 0)
            {
                return state with
                {
                    AnalysisSummary = summary,
                    Proposals = [],
                    TerminalStatus = ArchitectureWorkflowStatus.InsufficientEvidence,
                    ValidationDiagnostics =
                    [
                        "No MCP-accepted architecture proposal was produced for this attempt.",
                    ],
                };
            }

            return state with
            {
                AnalysisSummary = summary,
                Proposals = proposals,
                ValidationSucceeded = null,
                ValidationDiagnostics = [],
                TerminalStatus = null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Agent/provider details are hidden behind a controlled workflow diagnostic.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Fail(state, "Architecture proposal stage failed.");
        }
    }

    private AIFunction? GetTool(string name) =>
        mcpSession.Tools
            .OfType<AIFunction>()
            .SingleOrDefault(tool => string.Equals(tool.Name, name, StringComparison.Ordinal));

    private static ArchitectureWorkflowState Fail(
        ArchitectureWorkflowState state,
        string diagnostic) =>
        state with
        {
            TerminalStatus = ArchitectureWorkflowStatus.Failed,
            ValidationDiagnostics = [diagnostic],
        };

    private static bool TryGetStructuredContent(
        object? result,
        out JsonElement content)
    {
        if (result is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("structuredContent", out JsonElement structured))
            {
                content = structured;
                return true;
            }

            content = element;
            return true;
        }

        content = default;
        return false;
    }

    private static void AddValidationDiagnostics(
        List<string> diagnostics,
        string level,
        JsonElement content)
    {
        if (!content.TryGetProperty("diagnostics", out JsonElement items)
            || items.ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(level + ": LikeC4 validation failed.");
            return;
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (diagnostics.Count >= MaxDiagnostics)
            {
                break;
            }

            string code =
                item.TryGetProperty("code", out JsonElement codeElement)
                && codeElement.ValueKind == JsonValueKind.String
                    ? SanitizeDiagnostic(codeElement.GetString())
                    : "validation_error";
            string message =
                item.TryGetProperty("message", out JsonElement messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                    ? SanitizeDiagnostic(messageElement.GetString())
                    : "LikeC4 validation failed.";

            diagnostics.Add(level + ": " + code + " - " + message);
        }
    }

    private static string SanitizeDiagnostic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "validation_error";
        }

        string sanitized = new(
            value
                .Where(character => !char.IsControl(character) || character == ' ')
                .ToArray());

        return sanitized.Length <= MaxDiagnosticLength
            ? sanitized
            : sanitized[..MaxDiagnosticLength];
    }
}
