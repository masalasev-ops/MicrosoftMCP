namespace LearnMcpTutorial.Diagnostics;

/// <summary>
/// What kind of step occurred in the function invocation loop.
/// </summary>
public enum TraceStepType
{
    /// <summary>The LLM requested one or more tool calls.</summary>
    ToolCallsRequested,
    /// <summary>The LLM produced text (final answer or intermediate).</summary>
    TextProduced
}

/// <summary>
/// A single traced tool call — name and serialized arguments.
/// </summary>
/// <param name="FunctionName">The tool name (e.g. "microsoft_docs_search").</param>
/// <param name="FunctionArguments">JSON-serialized arguments.</param>
public sealed record TracedToolCall(string FunctionName, string FunctionArguments);

/// <summary>
/// One step in the function invocation loop, fired each time the
/// inner LLM is called. Contains either tool call requests or
/// produced text (or both, in edge cases).
/// </summary>
/// <param name="Type">Whether this step is tool calls or text.</param>
/// <param name="ToolCalls">Tool calls the LLM requested (empty if none).</param>
/// <param name="Text">Text the LLM produced (null if pure tool call response).</param>
public sealed record TraceStep(TraceStepType Type, IReadOnlyList<TracedToolCall> ToolCalls, string? Text);
