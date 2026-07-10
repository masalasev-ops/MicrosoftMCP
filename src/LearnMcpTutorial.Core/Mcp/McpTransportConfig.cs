namespace LearnMcpTutorial.Mcp;

/// <summary>
/// Describes which transport an <see cref="LearnMcpClient"/> should use to
/// reach an MCP server. The teaching point of the tutorial: swapping the
/// server (remote Learn ↔ local stdio) changes ONLY the transport — the
/// agent, the tool-calling loop, and everything downstream stay identical.
/// </summary>
public abstract record McpTransportConfig
{
    /// <summary>A short human-readable label for logs and UI.</summary>
    public abstract string DisplayName { get; }
}

/// <summary>
/// Streamable HTTP transport — used for remote MCP servers like the
/// Microsoft Learn MCP Server at learn.microsoft.com/api/mcp.
/// </summary>
/// <param name="Url">The MCP endpoint URL.</param>
/// <param name="MaxTokenBudget">
/// Optional cap on token count in search responses (appended as a query
/// parameter). Useful for keeping agentic loops within budget.
/// </param>
public sealed record HttpTransportConfig(string Url, int? MaxTokenBudget = null) : McpTransportConfig
{
    public override string DisplayName => "Streamable HTTP";
}

/// <summary>
/// Stdio transport — launches a local MCP server as a child process and
/// communicates over its standard input/output streams.
/// </summary>
/// <param name="Command">The executable to launch (e.g. "dotnet").</param>
/// <param name="Arguments">Arguments passed to the command.</param>
/// <param name="Label">A friendly name for the launched server.</param>
public sealed record StdioTransportConfig(
    string Command,
    IReadOnlyList<string> Arguments,
    string Label) : McpTransportConfig
{
    public override string DisplayName => "stdio (local process)";
}
