using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Repo2C4.Agent;

/// <summary>Entry point for the standalone Repo2C4 Agent host.</summary>
public static class Program
{
    private const int SuccessExitCode = 0;
    private const int FailureExitCode = 1;
    private const int UsageExitCode = 2;

    public static async Task<int> Main(string[] args)
    {
        using CancellationTokenSource shutdown = new();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            return await RunAsync(args, Console.Out, Console.Error, shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken,
        IAgentChatClientFactory? chatClientFactory = null,
        IRepo2C4AgentFactory? agentFactory = null,
        IAgentSessionRunner? sessionRunner = null,
        IAgentMcpSessionFactory? mcpSessionFactory = null,
        IArchitectureAnalysisWorkflowRunner? workflowRunner = null,
        IWriteApprovalRunner? writeApprovalRunner = null,
        TextReader? standardInput = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        if (args is ["--help"] or ["-h"])
        {
            WriteHelp(standardError);
            return SuccessExitCode;
        }

        if (!AgentHostOptions.TryParse(args, out AgentHostOptions? options, out string? error))
        {
            standardError.WriteLine(error);
            return UsageExitCode;
        }

        AgentHostOptions configuredOptions = options!;
        cancellationToken.ThrowIfCancellationRequested();

        AgentExecutionContext? execution =
            configuredOptions.Goal is null
                ? null
                : new AgentExecutionContext(AgentExecutionBudgets.FromOptions(configuredOptions));
        execution?.ConfigureStructuredLogging(standardError);
        configuredOptions = configuredOptions with
        {
            ExecutionContext = execution,
        };

        using CancellationTokenSource? deadline =
            execution?.CreateDeadlineSource(cancellationToken);
        CancellationToken executionToken = deadline?.Token ?? cancellationToken;

        chatClientFactory ??= new ProviderAgentChatClientFactory();
        agentFactory ??= new Repo2C4AgentFactory();
        sessionRunner ??= new AgentSessionRunner();
        mcpSessionFactory ??= new Repo2C4McpSessionFactory();
        workflowRunner ??= new ArchitectureAnalysisWorkflowRunner(sessionRunner);

        try
        {
            AgentChatClientCreation chatCreation = await chatClientFactory
                .CreateAsync(configuredOptions, executionToken)
                .ConfigureAwait(false);

            if (chatCreation.ChatClient is null)
            {
                standardError.WriteLine(
                    chatCreation.Diagnostic ??
                    "Repo2C4 Agent configuration error: the configured provider could not be created.");
                return UsageExitCode;
            }

            using IChatClient chatClient = chatCreation.ChatClient;

            AgentMcpSessionCreation mcpCreation = await mcpSessionFactory
                .CreateAsync(configuredOptions, executionToken)
                .ConfigureAwait(false);

            if (mcpCreation.Session is null)
            {
                standardError.WriteLine(
                    mcpCreation.Diagnostic ??
                    "Repo2C4 Agent MCP configuration error: the local MCP session could not be created.");
                return UsageExitCode;
            }

            IAgentMcpSession mcpSession = mcpCreation.Session;
            await using (mcpSession.ConfigureAwait(false))
            {
                AIAgent agent = agentFactory.Create(
                    chatClient,
                    configuredOptions,
                    mcpSession.Tools);

                if (configuredOptions.Goal is null)
                {
                    return SuccessExitCode;
                }

                ArchitectureWorkflowResult result = await workflowRunner
                    .RunAsync(
                        agent,
                        configuredOptions,
                        mcpSession,
                        executionToken)
                    .ConfigureAwait(false);

                standardOutput.WriteLine(result.ToDisplayText());

                if (result.Status is ArchitectureWorkflowStatus.Failed
                    or ArchitectureWorkflowStatus.ValidationFailed)
                {
                    return FailureExitCode;
                }

                if (configuredOptions.WriteDestination is null
                    || result.Status == ArchitectureWorkflowStatus.Cancelled)
                {
                    return SuccessExitCode;
                }

                writeApprovalRunner ??= new WriteApprovalRunner(
                    new ConsoleWriteApprovalPrompt(
                        standardInput ?? Console.In,
                        standardError));

                WriteApprovalResult writeResult = await writeApprovalRunner
                    .RunAsync(
                        chatClient,
                        configuredOptions,
                        mcpSession,
                        result,
                        executionToken)
                    .ConfigureAwait(false);

                standardOutput.WriteLine();
                standardOutput.WriteLine(writeResult.ToDisplayText());

                return writeResult.Status is WriteApprovalStatus.Failed
                    or WriteApprovalStatus.StalePreview
                    or WriteApprovalStatus.Conflict
                        ? FailureExitCode
                        : SuccessExitCode;
            }
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            bool deadlineExceeded =
                execution?.IsDeadlineExceeded is true
                && !cancellationToken.IsCancellationRequested;
            ArchitectureWorkflowStatus status = deadlineExceeded
                ? ArchitectureWorkflowStatus.Failed
                : ArchitectureWorkflowStatus.Cancelled;
            string terminalReason = deadlineExceeded
                ? "total_duration_exceeded"
                : "cancelled";

            if (execution is not null)
            {
                ArchitectureWorkflowResult cancelled = new(
                    status,
                    0,
                    string.Empty,
                    deadlineExceeded
                        ? ["Total execution-duration budget exceeded."]
                        : ["Agent execution was cancelled."])
                {
                    RunId = execution.RunId,
                    Counters = execution.SnapshotCounters(),
                    TerminalReason = terminalReason,
                };

                standardOutput.WriteLine(cancelled.ToDisplayText());
                execution.Complete(
                    deadlineExceeded ? "failed" : "cancelled",
                    terminalReason);
            }

            return deadlineExceeded ? FailureExitCode : SuccessExitCode;
        }
        catch (AgentProviderException exception)
        {
            standardError.WriteLine(exception.Message);
            return FailureExitCode;
        }
#pragma warning disable CA1031 // Process boundary intentionally hides provider/framework/MCP exception details and secrets.
        catch (Exception)
#pragma warning restore CA1031
        {
            standardError.WriteLine(
                "Repo2C4 Agent stopped because of an unexpected agent execution error.");
            return FailureExitCode;
        }
    }

    private static void WriteHelp(TextWriter standardError)
    {
        standardError.WriteLine("Repo2C4 Agent host");
        standardError.WriteLine(
            "Usage: repo2c4-agent --provider ollama|openai --model <model> --repository-root <absolute-path> --goal <objective> [--write-destination <relative-root>] [--c3-container <container-id>] [--max-validation-attempts 1-3] [--max-duration-seconds 1-1800] [--max-tool-calls 1-100] [--max-workflow-iterations 1-3] [--max-evidence-pages 1-50] [--max-response-chars 1024-100000] [--max-context-chars 4096-200000] [--mcp-server-path <absolute-path>] [--timeout-seconds 1-300]");
        standardError.WriteLine(
            "Agent Framework Workflows orchestrates proposal -> evidence report -> dry-run preview -> validation with bounded validation attempts and workflow iterations.");
        standardError.WriteLine(
            "--timeout-seconds bounds each provider request; --max-duration-seconds bounds the complete agent run. Tool calls, evidence pages, response/context size and workflow iterations also have safe configurable limits.");
        standardError.WriteLine(
            "Optional --write-destination enables a separate Agent Framework HITL approval after successful validation; C1/C2 are written under <destination>/c1 and <destination>/c2 only after explicit local approval.");
        standardError.WriteLine(
            "C3 is disabled unless --c3-container selects one container; the host never allows the model to select a different C3 target.");
        standardError.WriteLine(
            "If --mcp-server-path is omitted, the installed repo2c4-mcp command is used. A .dll path is launched with dotnet.");
        standardError.WriteLine(
            "Ollama defaults to http://127.0.0.1:11434/; override only with --endpoint using an HTTP loopback origin.");
        standardError.WriteLine(
            "OpenAI requires --allow-external-ai and reads OPENAI_API_KEY only from the Agent process environment; the MCP child does not inherit it.");
        standardError.WriteLine("Provider and model are mandatory and never inferred or silently defaulted.");
        standardError.WriteLine("Credentials are not accepted by this host command surface.");
    }
}
