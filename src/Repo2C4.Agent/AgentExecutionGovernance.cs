using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Repo2C4.Agent;

/// <summary>Explicit operational limits for one agent execution.</summary>
public sealed record AgentExecutionBudgets(
    TimeSpan TotalDuration,
    int MaxToolCalls,
    int MaxWorkflowIterations,
    int MaxEvidencePages,
    int MaxResponseCharacters,
    int MaxContextCharacters)
{
    public static AgentExecutionBudgets FromOptions(AgentHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new AgentExecutionBudgets(
            options.Timeout,
            options.MaxToolCalls,
            options.MaxWorkflowIterations,
            options.MaxEvidencePages,
            options.MaxResponseCharacters,
            options.MaxContextCharacters);
    }
}

/// <summary>Safe aggregate counters emitted with the terminal execution result.</summary>
public sealed record AgentExecutionCounters(
    int ToolCalls,
    int WorkflowIterations,
    int EvidencePages,
    long ResponseCharacters,
    long ContextCharacters);

/// <summary>Controlled failure raised when an operational budget is exhausted.</summary>
public sealed class AgentBudgetExceededException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Run-local operational governance, structured logging and OpenTelemetry-compatible activities.
/// No prompt, evidence content, tool arguments or credentials are accepted by this API.
/// </summary>
public sealed class AgentExecutionContext
{
    public const string ActivitySourceName = "Repo2C4.Agent";

    public static ActivitySource TelemetryActivitySource { get; } =
        new(ActivitySourceName, "1.0.0");

    private readonly AgentExecutionBudgets budgets;
    private readonly object logGate = new();
    private TextWriter? logWriter;
    private int toolCalls;
    private int workflowIterations;
    private int evidencePages;
    private long responseCharacters;
    private long contextCharacters;
    private int deadlineExceeded;

    public AgentExecutionContext(AgentExecutionBudgets budgets, string? runId = null)
    {
        this.budgets = budgets ?? throw new ArgumentNullException(nameof(budgets));
        RunId = string.IsNullOrWhiteSpace(runId)
            ? Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture)
            : runId;
    }

    public string RunId
    {
        get;
    }

    public AgentExecutionBudgets Budgets => budgets;

    public bool IsDeadlineExceeded => Volatile.Read(ref deadlineExceeded) != 0;

    public void ConfigureStructuredLogging(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        lock (logGate)
        {
            logWriter = writer;
        }

        Log(
            "run_started",
            stage: null,
            toolName: null,
            durationMilliseconds: null,
            result: "started",
            errorCode: null);
    }

    public CancellationTokenSource CreateDeadlineSource(CancellationToken parentToken)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        _ = source.Token.Register(
            () =>
            {
                if (!parentToken.IsCancellationRequested)
                {
                    Interlocked.Exchange(ref deadlineExceeded, 1);
                }
            });
        source.CancelAfter(budgets.TotalDuration);
        return source;
    }

    public AgentOperationScope BeginTool(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        int currentToolCalls = Interlocked.Increment(ref toolCalls);
        if (currentToolCalls > budgets.MaxToolCalls)
        {
            ThrowBudget("tool_calls_exceeded", "Tool-call budget exceeded.");
        }

        if (toolName is "get_evidence" or "get_snapshot")
        {
            int currentEvidencePages = Interlocked.Increment(ref evidencePages);
            if (currentEvidencePages > budgets.MaxEvidencePages)
            {
                ThrowBudget("evidence_pages_exceeded", "Evidence-page budget exceeded.");
            }
        }

        return BeginOperation("tool", toolName);
    }

    public AgentOperationScope BeginWorkflowStage(string stage, bool countsAsIteration = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        if (countsAsIteration)
        {
            int currentIterations = Interlocked.Increment(ref workflowIterations);
            if (currentIterations > budgets.MaxWorkflowIterations)
            {
                ThrowBudget("workflow_iterations_exceeded", "Workflow-iteration budget exceeded.");
            }
        }

        return BeginOperation(stage, toolName: null);
    }

    public void ObserveContext(string? context)
    {
        int length = context?.Length ?? 0;
        long total = Interlocked.Add(ref contextCharacters, length);

        if (total > budgets.MaxContextCharacters)
        {
            ThrowBudget("context_size_exceeded", "Context-size budget exceeded.");
        }
    }

    public void ObserveResponse(string? response)
    {
        int length = response?.Length ?? 0;
        long total = Interlocked.Add(ref responseCharacters, length);

        if (total > budgets.MaxResponseCharacters)
        {
            ThrowBudget("response_size_exceeded", "Response-size budget exceeded.");
        }
    }

    public void ObserveToolResponse(object? result)
    {
        int length = result switch
        {
            null => 0,
            string text => text.Length,
            JsonElement element => element.GetRawText().Length,
            _ => 0,
        };

        long total = Interlocked.Add(ref responseCharacters, length);
        if (total > budgets.MaxResponseCharacters)
        {
            ThrowBudget("response_size_exceeded", "Response-size budget exceeded.");
        }
    }

    public AgentExecutionCounters SnapshotCounters() =>
        new(
            Volatile.Read(ref toolCalls),
            Volatile.Read(ref workflowIterations),
            Volatile.Read(ref evidencePages),
            Interlocked.Read(ref responseCharacters),
            Interlocked.Read(ref contextCharacters));

    public void Complete(string result, string? errorCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        Log(
            "run_completed",
            stage: null,
            toolName: null,
            durationMilliseconds: null,
            result,
            errorCode);
    }

    private AgentOperationScope BeginOperation(string stage, string? toolName)
    {
        Activity? activity = TelemetryActivitySource.StartActivity(
            toolName is null ? "repo2c4.agent." + stage : "repo2c4.agent.tool",
            ActivityKind.Internal);

        activity?.SetTag("repo2c4.run_id", RunId);
        activity?.SetTag("repo2c4.stage", stage);
        if (toolName is not null)
        {
            activity?.SetTag("repo2c4.tool.name", toolName);
        }

        return new AgentOperationScope(this, stage, toolName, activity);
    }

    private void ThrowBudget(string code, string message)
    {
        Log(
            "budget_exceeded",
            stage: null,
            toolName: null,
            durationMilliseconds: null,
            result: "failed",
            errorCode: code);
        throw new AgentBudgetExceededException(code, message);
    }

    internal void LogOperation(
        string stage,
        string? toolName,
        long durationMilliseconds,
        string result,
        string? errorCode)
    {
        Log(
            "operation_completed",
            stage,
            toolName,
            durationMilliseconds,
            result,
            errorCode);
    }

    private void Log(
        string eventName,
        string? stage,
        string? toolName,
        long? durationMilliseconds,
        string result,
        string? errorCode)
    {
        TextWriter? writer;
        lock (logGate)
        {
            writer = logWriter;
        }

        if (writer is null)
        {
            return;
        }

        AgentExecutionCounters counters = SnapshotCounters();
        string line = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            runId = RunId,
            eventName,
            stage,
            toolName,
            durationMilliseconds,
            result,
            errorCode,
            counters.ToolCalls,
            counters.WorkflowIterations,
            counters.EvidencePages,
            counters.ResponseCharacters,
            counters.ContextCharacters,
        });

        lock (logGate)
        {
            writer.WriteLine(line);
        }
    }
}

/// <summary>One structured/logged activity scope without payload capture.</summary>
public sealed class AgentOperationScope : IDisposable
{
    private readonly AgentExecutionContext owner;
    private readonly string stage;
    private readonly string? toolName;
    private readonly Activity? activity;
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private int completed;

    internal AgentOperationScope(
        AgentExecutionContext owner,
        string stage,
        string? toolName,
        Activity? activity)
    {
        this.owner = owner;
        this.stage = stage;
        this.toolName = toolName;
        this.activity = activity;
    }

    public void Succeed() => Complete("completed", null);

    public void Fail(string errorCode) => Complete("failed", errorCode);

    public void Cancel() => Complete("cancelled", "cancelled");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref completed, 1) == 0)
        {
            stopwatch.Stop();
            owner.LogOperation(
                stage,
                toolName,
                stopwatch.ElapsedMilliseconds,
                "completed",
                null);
            activity?.SetTag("repo2c4.result", "completed");
            activity?.Dispose();
        }
    }

    private void Complete(string result, string? errorCode)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0)
        {
            return;
        }

        stopwatch.Stop();
        owner.LogOperation(
            stage,
            toolName,
            stopwatch.ElapsedMilliseconds,
            result,
            errorCode);
        activity?.SetTag("repo2c4.result", result);
        if (errorCode is not null)
        {
            activity?.SetTag("repo2c4.error_code", errorCode);
        }

        activity?.Dispose();
    }
}

/// <summary>Applies run budgets and safe telemetry to one MCP function.</summary>
internal sealed class GovernedMcpFunction(
    AIFunction inner,
    AgentExecutionContext execution)
    : DelegatingAIFunction(inner)
{
    private readonly AgentExecutionContext execution =
        execution ?? throw new ArgumentNullException(nameof(execution));

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        using AgentOperationScope operation = execution.BeginTool(Name);
        try
        {
            object? result = await base
                .InvokeCoreAsync(arguments, cancellationToken)
                .ConfigureAwait(false);
            execution.ObserveToolResponse(result);
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
#pragma warning disable CA1031 // Telemetry records only a controlled error code; the original exception still propagates.
        catch (Exception)
#pragma warning restore CA1031
        {
            operation.Fail("tool_failed");
            throw;
        }
    }
}

/// <summary>Adds bounded iteration accounting and stage telemetry around workflow operations.</summary>
internal sealed class GovernedArchitectureWorkflowOperations(
    IArchitectureWorkflowOperations inner,
    AgentExecutionContext execution) : IArchitectureWorkflowOperations
{
    private readonly IArchitectureWorkflowOperations inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly AgentExecutionContext execution =
        execution ?? throw new ArgumentNullException(nameof(execution));

    public ValueTask<ArchitectureWorkflowState> AnalyzeAsync(
        ArchitectureWorkflowState state,
        CancellationToken cancellationToken) =>
        RunAsync(
            "analysis",
            countsAsIteration: true,
            () => inner.AnalyzeAsync(state, cancellationToken));

    public ValueTask<ArchitectureWorkflowState> GetEvidenceReportAsync(
        ArchitectureWorkflowState state,
        CancellationToken cancellationToken) =>
        RunAsync(
            "evidence_report",
            countsAsIteration: false,
            () => inner.GetEvidenceReportAsync(state, cancellationToken));

    public ValueTask<ArchitectureWorkflowState> PreviewAsync(
        ArchitectureWorkflowState state,
        CancellationToken cancellationToken) =>
        RunAsync(
            "preview",
            countsAsIteration: false,
            () => inner.PreviewAsync(state, cancellationToken));

    public ValueTask<ArchitectureWorkflowState> ValidateAsync(
        ArchitectureWorkflowState state,
        CancellationToken cancellationToken) =>
        RunAsync(
            "validation",
            countsAsIteration: false,
            () => inner.ValidateAsync(state, cancellationToken));

    public ValueTask<ArchitectureWorkflowState> CorrectAsync(
        ArchitectureWorkflowState state,
        int attempt,
        CancellationToken cancellationToken) =>
        RunAsync(
            "correction",
            countsAsIteration: true,
            () => inner.CorrectAsync(state, attempt, cancellationToken));

    private async ValueTask<ArchitectureWorkflowState> RunAsync(
        string stage,
        bool countsAsIteration,
        Func<ValueTask<ArchitectureWorkflowState>> action)
    {
        using AgentOperationScope operation =
            execution.BeginWorkflowStage(stage, countsAsIteration);

        try
        {
            ArchitectureWorkflowState result = await action().ConfigureAwait(false);
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
#pragma warning disable CA1031 // Telemetry records only a controlled error code; the original exception still propagates.
        catch (Exception)
#pragma warning restore CA1031
        {
            operation.Fail("workflow_stage_failed");
            throw;
        }
    }
}
