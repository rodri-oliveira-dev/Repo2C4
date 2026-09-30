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
        IArchitectureAnalysisWorkflowRunner? workflowRunner = null)
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

        chatClientFactory ??= new ProviderAgentChatClientFactory();
        agentFactory ??= new Repo2C4AgentFactory();
        sessionRunner ??= new AgentSessionRunner();
        mcpSessionFactory ??= new Repo2C4McpSessionFactory();
        workflowRunner ??= new ArchitectureAnalysisWorkflowRunner(sessionRunner);

        try
        {
            AgentChatClientCreation chatCreation = await chatClientFactory
                .CreateAsync(configuredOptions, cancellationToken)
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
                .CreateAsync(configuredOptions, cancellationToken)
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
                        cancellationToken)
                    .ConfigureAwait(false);

                standardOutput.WriteLine(result.ToDisplayText());
                return result.Status is ArchitectureWorkflowStatus.Failed
                    or ArchitectureWorkflowStatus.ValidationFailed
                        ? FailureExitCode
                        : SuccessExitCode;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SuccessExitCode;
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
            "Usage: dotnet run --project src/Repo2C4.Agent -- --provider ollama|openai --model <model> --repository-root <absolute-path> --goal <objective> [--c3-container <container-id>] [--max-validation-attempts 1-3] [--mcp-server-path <absolute-path>] [--timeout-seconds 1-300]");
        standardError.WriteLine(
            "Agent Framework Workflows orchestrates proposal -> evidence report -> dry-run preview -> validation with a bounded validation-attempt budget (default 2, maximum 3).");
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
