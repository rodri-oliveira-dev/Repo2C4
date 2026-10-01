using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Repo2C4.Agent.Tests;

internal sealed class TestChatClient(string responseText) : IChatClient
{
    public IReadOnlyList<ChatMessage>? LastMessages
    {
        get;
        private set;
    }

    public ChatOptions? LastOptions
    {
        get;
        private set;
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastMessages = chatMessages.ToArray();
        LastOptions = options;

        return Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastMessages = chatMessages.ToArray();
        LastOptions = options;
        return EmptyResponses(cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        _ = serviceKey;
        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyResponses(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
