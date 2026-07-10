namespace LearnMcpTutorial.Mcp;

/// <summary>
/// Thrown when <see cref="LearnMcpClient.ConnectAsync"/> cannot reach an MCP
/// server. The message is written for the person running the app, not for a log
/// aggregator: it names the transport, says what went wrong, and suggests the
/// next thing to try. The original failure is always preserved as the inner
/// exception.
/// </summary>
public sealed class McpConnectionException : Exception
{
    public McpConnectionException(string message)
        : base(message)
    {
    }

    public McpConnectionException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
