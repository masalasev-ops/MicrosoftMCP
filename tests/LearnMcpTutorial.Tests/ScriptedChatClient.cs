using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace LearnMcpTutorial.Tests;

/// <summary>
/// A test double for <see cref="IChatClient"/> that returns a scripted sequence
/// of responses. Lets us exercise <c>DocsAgent</c> and the function-invocation
/// loop deterministically, with no network calls and no tokens spent.
///
/// It also records every set of messages it was called with, so tests can
/// assert on conversation history (multi-turn).
/// </summary>
public sealed class ScriptedChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _responses;

    /// <summary>Each entry is the full message list passed on one call.</summary>
    public List<List<ChatMessage>> ReceivedMessageSets { get; } = [];

    public int CallCount => ReceivedMessageSets.Count;

    public ScriptedChatClient(params ChatResponse[] responses)
        => _responses = new Queue<ChatResponse>(responses);

    /// <summary>Convenience: a plain assistant text response.</summary>
    public static ChatResponse Text(string text)
        => new(new ChatMessage(ChatRole.Assistant, text));

    /// <summary>Convenience: an assistant response requesting one tool call.</summary>
    public static ChatResponse ToolCall(string callId, string toolName, Dictionary<string, object?> args)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, toolName, args)]));

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ReceivedMessageSets.Add(messages.ToList());
        if (_responses.Count == 0)
            throw new InvalidOperationException("ScriptedChatClient ran out of scripted responses.");
        return Task.FromResult(_responses.Dequeue());
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
            yield return new ChatResponseUpdate(message.Role, message.Contents);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
