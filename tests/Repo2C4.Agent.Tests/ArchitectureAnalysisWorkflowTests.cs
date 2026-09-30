using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Xunit;

namespace Repo2C4.Agent.Tests;

public sealed class ArchitectureAnalysisWorkflowTests
{
    [Fact]
    public void WorkflowGraphIsExplicitAndBounded()
    {
        FakeOperations operations = new(true);
        ArchitectureAnalysisWorkflow subject = new(operations, maxValidationAttempts: 2);

        Workflow workflow = subject.Definition;
        string[] executors =
        [
            .. workflow.ReflectExecutors().Keys.Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            [
                "analysis",
                "correction-2",
                "evidence-report-1",
                "evidence-report-2",
                "finalize",
                "preview-1",
                "preview-2",
                "validation-1",
                "validation-2",
            ],
            executors);
        Assert.Equal("analysis", workflow.StartExecutorId);
    }

    [Fact]
    public async Task ValidProposalCompletesWithoutRetry()
    {
        FakeOperations operations = new(true);
        ArchitectureAnalysisWorkflow subject = new(operations, maxValidationAttempts: 3);

        ArchitectureWorkflowResult result = await subject.RunAsync(
            ArchitectureWorkflowState.Initial(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ArchitectureWorkflowStatus.RequiresReview, result.Status);
        Assert.Equal(1, result.ValidationAttempts);
        Assert.Equal(
            ["analysis", "report-1", "preview-1", "validate-1"],
            operations.Calls);
        Assert.DoesNotContain(
            operations.Calls,
            call => call.StartsWith("correction-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidProposalReceivesDiagnosticsAndCorrectsOnce()
    {
        FakeOperations operations = new(false, true);
        ArchitectureAnalysisWorkflow subject = new(operations, maxValidationAttempts: 2);

        ArchitectureWorkflowResult result = await subject.RunAsync(
            ArchitectureWorkflowState.Initial(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ArchitectureWorkflowStatus.RequiresReview, result.Status);
        Assert.Equal(2, result.ValidationAttempts);
        Assert.Equal(
            [
                "analysis",
                "report-1",
                "preview-1",
                "validate-1",
                "correction-2",
                "report-2",
                "preview-2",
                "validate-2",
            ],
            operations.Calls);
        Assert.Equal(
            ["C2: syntax_error - Invalid relationship syntax."],
            operations.CorrectionDiagnostics);
    }

    [Fact]
    public async Task InvalidProposalStopsAtConfiguredAttemptLimit()
    {
        FakeOperations operations = new(false, false, true);
        ArchitectureAnalysisWorkflow subject = new(operations, maxValidationAttempts: 2);

        ArchitectureWorkflowResult result = await subject.RunAsync(
            ArchitectureWorkflowState.Initial(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ArchitectureWorkflowStatus.ValidationFailed, result.Status);
        Assert.Equal(2, result.ValidationAttempts);
        Assert.Equal(2, operations.Calls.Count(call => call.StartsWith("validate-", StringComparison.Ordinal)));
        Assert.DoesNotContain("validate-3", operations.Calls);
    }

    [Fact]
    public async Task InsufficientEvidenceStopsWithStructuredResult()
    {
        FakeOperations operations = new(true)
        {
            InsufficientEvidence = true,
        };
        ArchitectureAnalysisWorkflow subject = new(operations, maxValidationAttempts: 2);

        ArchitectureWorkflowResult result = await subject.RunAsync(
            ArchitectureWorkflowState.Initial(),
            TestContext.Current.CancellationToken);

        Assert.Equal(ArchitectureWorkflowStatus.InsufficientEvidence, result.Status);
        Assert.Equal(1, result.ValidationAttempts);
        Assert.Contains(
            "No MCP-accepted architecture proposal",
            result.Diagnostics.Single(),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            operations.Calls,
            call => call.StartsWith("correction-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationReturnsControlledResultAndStopsWorkflow()
    {
        using CancellationTokenSource cancellation = new();
        FakeOperations operations = new(true)
        {
            CancellationSource = cancellation,
        };
        ArchitectureAnalysisWorkflow subject = new(operations, maxValidationAttempts: 3);

        ArchitectureWorkflowResult result = await subject.RunAsync(
            ArchitectureWorkflowState.Initial(),
            cancellation.Token);

        Assert.Equal(ArchitectureWorkflowStatus.Cancelled, result.Status);
        Assert.Equal(0, result.ValidationAttempts);
        Assert.Equal(["analysis"], operations.Calls);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("3", 3)]
    public void ValidationAttemptBudgetIsExplicitlyConfigurable(
        string value,
        int expected)
    {
        bool parsed = AgentHostOptions.TryParse(
            [
                "--provider", "ollama",
                "--model", "model",
                "--goal", "Document architecture",
                "--max-validation-attempts", value,
            ],
            out AgentHostOptions? options,
            out string? error);

        Assert.True(parsed, error);
        Assert.NotNull(options);
        Assert.Equal(expected, options.MaxValidationAttempts);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4")]
    [InlineData("invalid")]
    public void ValidationAttemptBudgetRejectsUnsafeValues(string value)
    {
        bool parsed = AgentHostOptions.TryParse(
            [
                "--provider", "ollama",
                "--model", "model",
                "--goal", "Document architecture",
                "--max-validation-attempts", value,
            ],
            out _,
            out string? error);

        Assert.False(parsed);
        Assert.Contains("--max-validation-attempts", error, StringComparison.Ordinal);
    }

    private static AgentArchitectureProposal CreateProposal() =>
        new(
            "C2",
            "snap-1",
            ParseJson(
                """
                {
                  "schemaVersion": "1.0",
                  "level": "C2",
                  "snapshot": {
                    "schemaVersion": "1.0",
                    "repositoryId": "repo_test",
                    "files": [],
                    "evidence": [],
                    "diagnostics": []
                  },
                  "elements": [
                    {
                      "id": "el_system",
                      "kind": "softwareSystem",
                      "name": "Candidate system",
                      "evidenceIds": [],
                      "status": "requiresReview",
                      "reviewReason": "The focal boundary requires human review."
                    }
                  ],
                  "relations": []
                }
                """),
            null);

    private static JsonElement ParseJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakeOperations(params bool[] validationResults)
        : IArchitectureWorkflowOperations
    {
        private readonly Queue<bool> validationResults = new(validationResults);

        public List<string> Calls
        {
            get;
        } = [];

        public IReadOnlyList<string> CorrectionDiagnostics
        {
            get;
            private set;
        } = [];

        public bool InsufficientEvidence
        {
            get;
            init;
        }

        public CancellationTokenSource? CancellationSource
        {
            get;
            init;
        }

        public async ValueTask<ArchitectureWorkflowState> AnalyzeAsync(
            ArchitectureWorkflowState state,
            CancellationToken cancellationToken)
        {
            Calls.Add("analysis");

            if (CancellationSource is not null)
            {
                CancellationSource.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (InsufficientEvidence)
            {
                return state with
                {
                    AnalysisSummary = "No architecture proposal could be supported.",
                    TerminalStatus = ArchitectureWorkflowStatus.InsufficientEvidence,
                    ValidationDiagnostics =
                    [
                        "No MCP-accepted architecture proposal was produced for this attempt.",
                    ],
                };
            }

            return state with
            {
                Proposals = [CreateProposal()],
                AnalysisSummary = Summary,
            };
        }

        public ValueTask<ArchitectureWorkflowState> GetEvidenceReportAsync(
            ArchitectureWorkflowState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("report-" + state.Attempt);

            return ValueTask.FromResult(
                state.TerminalStatus is null
                    ? state with
                    {
                        EvidenceReports = ["C2 evidence report completed."],
                    }
                    : state);
        }

        public ValueTask<ArchitectureWorkflowState> PreviewAsync(
            ArchitectureWorkflowState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("preview-" + state.Attempt);

            return ValueTask.FromResult(
                state.TerminalStatus is null
                    ? state with
                    {
                        PreviewSummaries = ["C2 LikeC4 preview completed."],
                    }
                    : state);
        }

        public ValueTask<ArchitectureWorkflowState> ValidateAsync(
            ArchitectureWorkflowState state,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("validate-" + state.Attempt);

            if (state.TerminalStatus is not null)
            {
                return ValueTask.FromResult(state);
            }

            bool isValid = validationResults.Count == 0 || validationResults.Dequeue();
            return ValueTask.FromResult(
                state with
                {
                    ValidationSucceeded = isValid,
                    ValidationDiagnostics = isValid
                        ? []
                        : ["C2: syntax_error - Invalid relationship syntax."],
                });
        }

        public ValueTask<ArchitectureWorkflowState> CorrectAsync(
            ArchitectureWorkflowState state,
            int attempt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("correction-" + attempt);
            CorrectionDiagnostics = state.ValidationDiagnostics;

            return ValueTask.FromResult(
                state with
                {
                    Attempt = attempt,
                    Proposals = [CreateProposal()],
                    AnalysisSummary = Summary,
                    ValidationSucceeded = null,
                    ValidationDiagnostics = [],
                });
        }

        private const string Summary =
            """
            Confirmed facts
            Static repository facts were observed.

            Requires review
            The focal boundary remains a hypothesis.

            Diagnostics/blockers
            Runtime behavior is not observed.

            Proposal
            A review-only C2 proposal was produced.
            """;
    }
}
