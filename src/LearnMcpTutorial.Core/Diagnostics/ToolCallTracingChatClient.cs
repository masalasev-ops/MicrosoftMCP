using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace LearnMcpTutorial.Diagnostics;

/// <summary>
/// An <see cref="IChatClient"/> wrapper that fires a callback on each
/// <see cref="GetResponseAsync"/> call, allowing the caller to observe
/// tool-call requests as the function invocation loop runs.
///
/// Insert this between <see cref="FunctionInvokingChatClient"/> and the
/// raw LLM provider via <c>ChatClientBuilder.Use()</c>.
/// </summary>
public sealed class ToolCallTracingChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly Action<TraceStep>? _onStep;

    /// <summary>
    /// Creates a tracing wrapper around an inner chat client.
    /// </summary>
    /// <param name="inner">The raw LLM provider.</param>
    /// <param name="onStep">
    /// Optional callback invoked on each <see cref="GetResponseAsync"/> call.
    /// Called from a thread-pool thread — marshalling to the UI thread
    /// is the caller's responsibility.
    /// </param>
    public ToolCallTracingChatClient(IChatClient inner, Action<TraceStep>? onStep)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _onStep = onStep;
    }

    /// <inheritdoc />
    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _inner.GetResponseAsync(messages, options, cancellationToken);
        FireStep(response);
        return response;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Observe streaming too: pass each update through immediately (so the UI
        // streams tokens in real time), while accumulating them. When the inner
        // stream for this round-trip completes, reconstruct the response and fire
        // the SAME trace callback as the non-streaming path.
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            updates.Add(update);
            yield return update;
        }

        if (_onStep is not null && updates.Count > 0)
            FireStep(updates.ToChatResponse());
    }

    /// <summary>
    /// Classifies a response (tool calls vs text) and fires the trace callback.
    /// Shared by the streaming and non-streaming paths.
    /// </summary>
    private void FireStep(ChatResponse response)
    {
        if (_onStep is null) return;

        var toolCalls = new List<TracedToolCall>();
        string? text = null;

        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent fcc)
                {
                    var args = fcc.Arguments is not null
                        ? System.Text.Json.JsonSerializer.Serialize(fcc.Arguments)
                        : "{}";
                    toolCalls.Add(new TracedToolCall(fcc.Name ?? "unknown", args));
                }
                else if (content is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text))
                {
                    text = (text is null) ? tc.Text : text + tc.Text;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(response.Text))
            text = response.Text;

        if (toolCalls.Count > 0 || text is not null)
        {
            var stepType = toolCalls.Count > 0
                ? TraceStepType.ToolCallsRequested
                : TraceStepType.TextProduced;
            _onStep(new TraceStep(stepType, toolCalls, text));
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType, object? key = null) => _inner.GetService(serviceType, key);

    /// <inheritdoc />
    public void Dispose() => _inner.Dispose();
}
