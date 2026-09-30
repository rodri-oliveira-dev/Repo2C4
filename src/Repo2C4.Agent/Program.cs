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
        IAgentSessionRunner? sessionRunner = null)
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

        // Phase 7 starts with a provider/model-aware host. Concrete provider adapters are issue #35.
        // A configuration-only startup is therefore valid without contacting a provider.
        if (configuredOptions.Prompt is null)
        {
            return SuccessExitCode;
        }

        chatClientFactory ??= new UnavailableAgentChatClientFactory();
        agentFactory ??= new Repo2C4AgentFactory();
        sessionRunner ??= new AgentSessionRunner();

        try
        {
            AgentChatClientCreation creation = await chatClientFactory
                .CreateAsync(configuredOptions, cancellationToken)
                .ConfigureAwait(false);

            if (creation.ChatClient is null)
            {
                standardError.WriteLine(
                    creation.Diagnostic ??
                    "Repo2C4 Agent configuration error: the configured provider could not be created.");
                return UsageExitCode;
            }

            using IChatClient chatClient = creation.ChatClient;
            AIAgent agent = agentFactory.Create(chatClient, configuredOptions);
            string response = await sessionRunner
                .RunAsync(agent, configuredOptions.Prompt, cancellationToken)
                .ConfigureAwait(false);

            standardOutput.WriteLine(response);
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return SuccessExitCode;
        }
#pragma warning disable CA1031 // Process boundary intentionally hides provider/framework exception details and secrets.
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
            "Usage: dotnet run --project src/Repo2C4.Agent -- --provider <provider> --model <model> [--prompt <text>]");
        standardError.WriteLine("Provider and model are mandatory and never inferred or silently defaulted.");
        standardError.WriteLine("Credentials are not accepted by this host command surface.");
    }
}
