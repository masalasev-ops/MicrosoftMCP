using System.Runtime.CompilerServices;
using LearnMcpTutorial.Diagnostics;
using LearnMcpTutorial.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace LearnMcpTutorial.Agent;

/// <summary>
/// An AI agent that answers developer questions grounded in current
/// Microsoft Learn documentation. Uses Microsoft.Extensions.AI's automatic
/// function invocation to call MCP tools — no hand-written tool-calling loop.
///
/// The agent is <b>stateful</b>: it keeps a running conversation history so
/// follow-up questions have context (multi-turn). Call
/// <see cref="ResetConversation"/> to start fresh.
///
/// Every answer includes cited learn.microsoft.com source URLs.
/// </summary>
public sealed class DocsAgent
{
    private const string SystemPrompt = """
        You are a helpful assistant for .NET and Azure developers.
        Your answers MUST be grounded in current Microsoft Learn documentation.

        Rules you must follow:
        1. Use the available tools to search Microsoft Learn for relevant,
           authoritative documentation before answering.
        2. Fetch key documentation pages to confirm exact details.
        3. Every factual claim MUST be backed by a cited source.
        4. At the end of your answer, list all learn.microsoft.com URLs
           you retrieved and used, under a "## Sources" heading.
        5. For code samples (Azure CLI, C#, etc.), verify the exact
           commands/syntax against the fetched docs — do NOT guess.
        6. If docs are ambiguous, say so rather than inventing an answer.
        """;

    private readonly IChatClient _chatClient;
    private readonly LearnMcpClient _mcpClient;
    private readonly ILogger<DocsAgent> _logger;
    private readonly int _maxToolIterations;

    // Running conversation history. Index 0 is always the system prompt.
    // Subsequent turns (user question + assistant/tool messages) are appended
    // so multi-turn follow-ups retain context.
    private readonly List<ChatMessage> _history = [new(ChatRole.System, SystemPrompt)];

    /// <param name="innerChatClient">
    /// The raw LLM provider <see cref="IChatClient"/> (OpenAI, Ollama, etc.).
    /// </param>
    /// <param name="mcpClient">
    /// The connected <see cref="LearnMcpClient"/> whose tools are made available.
    /// </param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="maxToolIterations">Max tool-calling round-trips (default 10).</param>
    /// <param name="onToolStep">
    /// Optional callback fired on each round-trip of the function invocation loop
    /// (works for both non-streaming and streaming). Used by GUI clients for a
    /// live tool-call trace. Runs on thread-pool threads — marshal to the UI
    /// thread yourself.
    /// </param>
    public DocsAgent(
        IChatClient innerChatClient,
        LearnMcpClient mcpClient,
        ILogger<DocsAgent> logger,
        int maxToolIterations = 10,
        Action<TraceStep>? onToolStep = null)
    {
        ArgumentNullException.ThrowIfNull(innerChatClient);
        ArgumentNullException.ThrowIfNull(mcpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _mcpClient = mcpClient;
        _maxToolIterations = maxToolIterations;

        // Pipeline (outermost wraps innermost):
        //   FunctionInvokingChatClient → ToolCallTracingChatClient (opt) → raw LLM
        //
        // ChatClientBuilder applies the FIRST-registered middleware as the
        // OUTERMOST layer. So we register UseFunctionInvocation first (outer)
        // and the tracing wrapper second (inner). This places the tracer INSIDE
        // the tool-calling loop, so it fires once per round-trip — the LLM's raw
        // tool-call request, then its text — giving a live, incremental trace
        // (both for non-streaming and streaming). Registering it the other way
        // would only report a single aggregated step at the end.
        var builder = innerChatClient.AsBuilder()
            .UseFunctionInvocation(configure: client =>
            {
                client.MaximumIterationsPerRequest = maxToolIterations;
            });

        if (onToolStep is not null)
        {
            builder.Use(inner => new ToolCallTracingChatClient(inner, onToolStep));
        }

        _chatClient = builder.Build();

        _logger.LogInformation(
            "DocsAgent initialized with {MaxIterations} max tool iterations",
            maxToolIterations);
    }

    /// <summary>
    /// The number of turns exchanged so far (excludes the system prompt).
    /// </summary>
    public int TurnCount => _history.Count(m => m.Role == ChatRole.User);

    /// <summary>
    /// Clears the conversation history, keeping only the system prompt.
    /// Call this to start a fresh, context-free conversation.
    /// </summary>
    public void ResetConversation()
    {
        _history.RemoveRange(1, _history.Count - 1);
        _logger.LogInformation("Conversation history reset");
    }

    /// <summary>
    /// Runs the agent for a single question and returns a grounded answer
    /// with cited learn.microsoft.com sources. Appends to the conversation
    /// history for multi-turn context.
    /// </summary>
    /// <param name="question">The developer question to answer.</param>
    /// <param name="tools">
    /// The tools to make available. <see cref="ModelContextProtocol.Client.McpClientTool"/>
    /// extends <see cref="AIFunction"/>, so pass the client's discovered tools
    /// directly. Typed as <see cref="AIFunction"/> so the agent is testable with
    /// fake tools.
    /// </param>
    /// <param name="modelId">Optional model override.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<GroundedAnswer> AskAsync(
        string question,
        IEnumerable<AIFunction> tools,
        string? modelId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var options = BuildOptions(tools, modelId, out var toolCount);
        _history.Add(new ChatMessage(ChatRole.User, question));
        _logger.LogInformation("Processing question ({ToolCount} tools available): {Question}", toolCount, question);

        ChatResponse response;
        try
        {
            response = await _chatClient.GetResponseAsync(_history, options, cancellationToken);
        }
        catch (Exception ex)
        {
            // --- Rule 2: Stale-schema retry ---
            // If the turn failed (e.g. a tool's schema changed since we last
            // listed), refresh the tool cache and retry exactly once.
            _logger.LogWarning(ex, "First attempt failed — may be a stale tool schema. Refreshing tools and retrying once.");

            var refreshed = await _mcpClient.TryRefreshAndRetryAsync(cancellationToken);
            if (refreshed)
            {
                options.Tools = [.. _mcpClient.Tools];
                _logger.LogInformation("Retrying with {ToolCount} refreshed tools", _mcpClient.Tools.Count);
                try
                {
                    response = await _chatClient.GetResponseAsync(_history, options, cancellationToken);
                }
                catch (Exception retryEx)
                {
                    _history.RemoveAt(_history.Count - 1); // roll back the user message
                    _logger.LogError(retryEx, "Retry also failed");
                    throw new AgentException(
                        "The agent encountered an error even after refreshing tools.", retryEx);
                }
            }
            else
            {
                _history.RemoveAt(_history.Count - 1); // roll back the user message
                _logger.LogError(ex, "Tool refresh failed — cannot retry");
                throw new AgentException(
                    "The agent encountered an error and tool refresh also failed.", ex);
            }
        }

        // Persist the assistant/tool messages so the next turn has context.
        _history.AddRange(response.Messages);

        var answer = response.Text ?? string.Empty;
        var citedUrls = ExtractCitedUrls(answer);
        _logger.LogInformation("Answer received ({Length} chars, {UrlCount} cited sources)", answer.Length, citedUrls.Count);

        return new GroundedAnswer(answer, [.. citedUrls]);
    }

    /// <summary>
    /// Streaming variant of <see cref="AskAsync"/>. Yields answer text as it is
    /// produced by the LLM (token deltas), enabling real-time UI updates. The
    /// tool-calling loop and tracing callback still run per round-trip.
    /// After enumeration completes, the turn is persisted to history.
    /// </summary>
    /// <remarks>
    /// Extract cited sources from the accumulated text with
    /// <see cref="ExtractCitedUrls"/> once streaming finishes.
    /// </remarks>
    public async IAsyncEnumerable<string> AskStreamingAsync(
        string question,
        IEnumerable<AIFunction> tools,
        string? modelId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        var options = BuildOptions(tools, modelId, out var toolCount);
        _history.Add(new ChatMessage(ChatRole.User, question));
        _logger.LogInformation("Processing (streaming) question ({ToolCount} tools): {Question}", toolCount, question);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in _chatClient.GetStreamingResponseAsync(_history, options, cancellationToken))
        {
            updates.Add(update);
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
        }

        // Reconstruct the full response and persist it for multi-turn context.
        var response = updates.ToChatResponse();
        _history.AddRange(response.Messages);
        _logger.LogInformation("Streaming answer complete ({Length} chars)", response.Text?.Length ?? 0);
    }

    private ChatOptions BuildOptions(IEnumerable<AIFunction> tools, string? modelId, out int toolCount)
    {
        var toolList = tools.ToList();
        toolCount = toolList.Count;
        var options = new ChatOptions { Tools = [.. toolList] };
        if (!string.IsNullOrWhiteSpace(modelId))
            options.ModelId = modelId;
        return options;
    }

    /// <summary>
    /// Extracts learn.microsoft.com URLs from answer text. Public + static so
    /// callers (e.g. a streaming UI) can pull sources from accumulated text.
    /// </summary>
    public static HashSet<string> ExtractCitedUrls(string text)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return urls;

        var matches = System.Text.RegularExpressions.Regex.Matches(
            text, @"https?://learn\.microsoft\.com/[^\s\)\]\>,\""]+");

        foreach (System.Text.RegularExpressions.Match match in matches)
            urls.Add(match.Value.TrimEnd('.', ',', ';', ':'));

        return urls;
    }
}

/// <summary>
/// The result of a DocsAgent query — the answer text and the set of
/// learn.microsoft.com source URLs it cited.
/// </summary>
public sealed record GroundedAnswer(string Answer, HashSet<string> CitedUrls);

/// <summary>
/// Exception thrown when the agent encounters a non-recoverable error.
/// </summary>
public sealed class AgentException : Exception
{
    public AgentException(string message, Exception? inner = null)
        : base(message, inner) { }
}
