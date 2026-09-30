using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Repo2C4.Agent;

/// <summary>Creates supported provider clients behind the common <see cref="IChatClient"/> contract.</summary>
public sealed class ProviderAgentChatClientFactory : IAgentChatClientFactory
{
    private readonly Func<Uri, string, IChatClient> createOllamaClient;
    private readonly Func<string, string, IChatClient> createOpenAiClient;
    private readonly Func<string, string?> getEnvironmentVariable;

    public ProviderAgentChatClientFactory(
        Func<Uri, string, IChatClient>? createOllamaClient = null,
        Func<string, string, IChatClient>? createOpenAiClient = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        this.createOllamaClient = createOllamaClient ?? CreateOllamaClient;
        this.createOpenAiClient = createOpenAiClient ?? CreateOpenAiClient;
        this.getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    public ValueTask<AgentChatClientCreation> CreateAsync(
        AgentHostOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        IChatClient? inner = null;
        string providerDisplayName;

        try
        {
            switch (options.Provider)
            {
                case AgentHostOptions.OllamaProvider:
                    Uri endpoint = options.Endpoint ?? AgentHostOptions.DefaultOllamaEndpoint;
                    inner = createOllamaClient(endpoint, options.Model);
                    providerDisplayName = "Ollama";
                    break;

                case AgentHostOptions.OpenAiProvider:
                    if (!options.AllowExternalAi)
                    {
                        return ValueTask.FromResult(
                            AgentChatClientCreation.Failure(
                                "Repo2C4 Agent configuration error: OpenAI requires explicit --allow-external-ai consent."));
                    }

                    string? apiKey = getEnvironmentVariable(AgentHostOptions.OpenAiApiKeyEnvironmentVariable);
                    if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl))
                    {
                        return ValueTask.FromResult(
                            AgentChatClientCreation.Failure(
                                "Repo2C4 Agent configuration error: OPENAI_API_KEY is required in the process environment."));
                    }

                    inner = createOpenAiClient(options.Model, apiKey);
                    providerDisplayName = "OpenAI";
                    break;

                default:
                    return ValueTask.FromResult(
                        AgentChatClientCreation.Failure(
                            "Repo2C4 Agent configuration error: supported providers are ollama and openai."));
            }

#pragma warning disable CA2000 // Ownership transfers to AgentChatClientCreation and is disposed by the host.
            IChatClient bounded = new BoundedProviderChatClient(
                inner,
                providerDisplayName,
                options.Timeout,
                options.ExecutionContext);
#pragma warning restore CA2000
            inner = null;
            return ValueTask.FromResult(AgentChatClientCreation.Success(bounded));
        }
        catch (ArgumentException)
        {
            inner?.Dispose();
            return ValueTask.FromResult(
                AgentChatClientCreation.Failure(
                    "Repo2C4 Agent configuration error: the selected provider/model configuration is invalid."));
        }
#pragma warning disable CA1031 // Provider constructors are an external boundary; details may contain credentials or endpoint data.
        catch (Exception)
#pragma warning restore CA1031
        {
            inner?.Dispose();
            return ValueTask.FromResult(
                AgentChatClientCreation.Failure(
                    "Repo2C4 Agent configuration error: the selected provider could not be initialized."));
        }
    }

    private static IChatClient CreateOllamaClient(Uri endpoint, string model)
    {
        HttpClient transport = new()
        {
            BaseAddress = endpoint,
            Timeout = Timeout.InfiniteTimeSpan,
        };

        try
        {
            return new OwnedHttpClientChatClient(
                new OllamaApiClient(transport, model),
                transport);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    private static IChatClient CreateOpenAiClient(string model, string apiKey) =>
        new OpenAI.Chat.ChatClient(model, apiKey).AsIChatClient();
}

internal sealed class BoundedProviderChatClient(
    IChatClient inner,
    string providerDisplayName,
    TimeSpan timeout,
    AgentExecutionContext? execution = null) : IChatClient
{
    private readonly IChatClient inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly string providerDisplayName =
        string.IsNullOrWhiteSpace(providerDisplayName)
            ? throw new ArgumentException("Provider display name is required.", nameof(providerDisplayName))
            : providerDisplayName;
    private readonly TimeSpan timeout =
        timeout > TimeSpan.Zero
            ? timeout
            : throw new ArgumentOutOfRangeException(nameof(timeout));
    private readonly AgentExecutionContext? execution = execution;

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatMessages);

        IReadOnlyList<ChatMessage> messages = chatMessages as IReadOnlyList<ChatMessage>
            ?? [.. chatMessages];
        ObserveProviderContext(messages, options);

        using CancellationTokenSource deadline = CreateDeadline(cancellationToken);
        try
        {
            return await inner
                .GetResponseAsync(messages, options, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new AgentProviderException(
                providerDisplayName + " request timed out.",
                exception);
        }
#pragma warning disable CA1031 // Provider exceptions are sanitized at this boundary before reaching stdout/stderr.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            throw new AgentProviderException(
                providerDisplayName + " request failed.",
                exception);
        }
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatMessages);
        IReadOnlyList<ChatMessage> messages = chatMessages as IReadOnlyList<ChatMessage>
            ?? [.. chatMessages];
        ObserveProviderContext(messages, options);
        return StreamAsync(messages, options, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceType.IsInstanceOfType(this)
            ? this
            : inner.GetService(serviceType, serviceKey);
    }

    public void Dispose() => inner.Dispose();

    private async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CreateDeadline(cancellationToken);

        IAsyncEnumerable<ChatResponseUpdate> updates;
        try
        {
            updates = inner.GetStreamingResponseAsync(chatMessages, options, deadline.Token);
        }
#pragma warning disable CA1031 // Provider exceptions are sanitized at this boundary before reaching stdout/stderr.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            throw Sanitize(exception, cancellationToken);
        }

        IAsyncEnumerator<ChatResponseUpdate> enumerator =
            updates.GetAsyncEnumerator(deadline.Token);
        await using (enumerator.ConfigureAwait(false))
        {
            while (await MoveNextAsync(enumerator, cancellationToken).ConfigureAwait(false))
            {
                yield return enumerator.Current;
            }
        }
    }

    private async ValueTask<bool> MoveNextAsync(
        IAsyncEnumerator<ChatResponseUpdate> enumerator,
        CancellationToken cancellationToken)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Provider exceptions are sanitized at this boundary before reaching stdout/stderr.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            throw Sanitize(exception, cancellationToken);
        }
    }

    private Exception Sanitize(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return exception;
        }

        if (exception is OperationCanceledException)
        {
            return new AgentProviderException(
                providerDisplayName + " request timed out.",
                exception);
        }

        return new AgentProviderException(
            providerDisplayName + " request failed.",
            exception);
    }

    private void ObserveProviderContext(
        IReadOnlyList<ChatMessage> messages,
        ChatOptions? options)
    {
        if (execution is null)
        {
            return;
        }

        long characters = 0;
        foreach (ChatMessage message in messages)
        {
            characters += message.Role.ToString().Length;
            foreach (AIContent content in message.Contents)
            {
                characters += CountContentCharacters(content);
            }
        }

        if (options is not null)
        {
            characters += options.Instructions?.Length ?? 0;
            characters += options.ModelId?.Length ?? 0;
            foreach (AITool tool in options.Tools ?? [])
            {
                characters += tool.Name.Length;
                characters += tool.Description?.Length ?? 0;
                if (tool is AIFunction function)
                {
                    characters += function.JsonSchema.GetRawText().Length;
                }
            }
        }

        execution.ObserveContextCharacters(characters);
    }

    private static int CountContentCharacters(AIContent content) =>
        content switch
        {
            TextContent text => text.Text?.Length ?? 0,
            FunctionCallContent call =>
                call.Name.Length
                + call.CallId.Length
                + CountValueCharacters(call.Arguments),
            FunctionResultContent result =>
                result.CallId.Length
                + CountValueCharacters(result.Result),
            _ => CountValueCharacters(content),
        };

    private static int CountValueCharacters(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        if (value is string text)
        {
            return text.Length;
        }

        if (value is JsonElement element)
        {
            return element.GetRawText().Length;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Serialize(value).Length;
        }
#pragma warning disable CA1031 // Counting is best-effort; unsupported metadata falls back to ToString without logging payloads.
        catch (Exception)
#pragma warning restore CA1031
        {
            return value.ToString()?.Length ?? 0;
        }
    }

    private CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }
}

internal sealed class AgentProviderException(string message, Exception innerException)
    : Exception(message, innerException);


internal sealed class OwnedHttpClientChatClient(
    IChatClient inner,
    HttpClient transport) : IChatClient
{
    private readonly IChatClient inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly HttpClient transport =
        transport ?? throw new ArgumentNullException(nameof(transport));
    private int disposed;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        inner.GetResponseAsync(chatMessages, options, cancellationToken);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        inner.GetStreamingResponseAsync(chatMessages, options, cancellationToken);

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceType.IsInstanceOfType(this)
            ? this
            : inner.GetService(serviceType, serviceKey);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        inner.Dispose();
        transport.Dispose();
    }
}
