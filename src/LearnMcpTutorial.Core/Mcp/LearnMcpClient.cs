using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace LearnMcpTutorial.Mcp;

/// <summary>
/// Connects to an MCP server (remote over Streamable HTTP, or local over
/// stdio), discovers tools at runtime, and exposes them as <see cref="AIFunction"/>
/// instances ready for use with Microsoft.Extensions.AI function invocation.
///
/// The transport is chosen by the <see cref="McpTransportConfig"/> passed in.
/// Everything after transport construction — client creation, tool discovery,
/// notification handling, retry — is identical regardless of transport. That
/// is the tutorial's key lesson: MCP clients are server-agnostic.
///
/// Implements Microsoft's mandatory custom-client rules:
///   1. Discover tools dynamically via tools/list at runtime.
///   2. Refresh + retry if a tool call fails with a stale-schema error.
///   3. Handle listChanged notifications by refreshing the tool cache.
/// </summary>
public sealed class LearnMcpClient : IAsyncDisposable
{
    private readonly ILogger<LearnMcpClient> _logger;
    private readonly McpTransportConfig _transportConfig;
    private readonly Lock _lock = new();

    private McpClient? _client;
    private IList<McpClientTool> _tools = Array.Empty<McpClientTool>();
    private bool _connected;

    /// <summary>
    /// Creates a client for the given transport (HTTP or stdio).
    /// </summary>
    public LearnMcpClient(ILogger<LearnMcpClient> logger, McpTransportConfig transportConfig)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _transportConfig = transportConfig ?? throw new ArgumentNullException(nameof(transportConfig));
    }

    /// <summary>
    /// Convenience overload for the common HTTP case (backwards compatible).
    /// </summary>
    public LearnMcpClient(ILogger<LearnMcpClient> logger, string mcpUrl, int? maxTokenBudget = null)
        : this(logger, new HttpTransportConfig(
            string.IsNullOrWhiteSpace(mcpUrl)
                ? throw new ArgumentException("MCP endpoint URL is required.", nameof(mcpUrl))
                : mcpUrl,
            maxTokenBudget))
    {
    }

    /// <summary>
    /// The currently discovered tools. Each <see cref="McpClientTool"/> is an <see cref="AIFunction"/>
    /// that can be passed directly to <see cref="ChatOptions.Tools"/>.
    /// </summary>
    public IList<McpClientTool> Tools
    {
        get
        {
            lock (_lock)
            {
                return _tools;
            }
        }
    }

    /// <summary>
    /// Whether the client has successfully connected and discovered tools.
    /// </summary>
    public bool Connected => Volatile.Read(ref _connected);

    /// <summary>
    /// A short description of the transport this client uses (for logs/UI).
    /// </summary>
    public string TransportDescription => _transportConfig.DisplayName;

    /// <summary>
    /// Connects to the configured MCP server and discovers available tools.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        // ─────────────────────────────────────────────────────────────────
        // THE ONLY THING THAT DIFFERS BETWEEN SERVERS: the transport.
        // Build an IClientTransport from the config, then everything below
        // is identical whether we're talking to the remote Learn server or
        // a local stdio server.
        // ─────────────────────────────────────────────────────────────────
        IClientTransport transport = _transportConfig switch
        {
            HttpTransportConfig http => BuildHttpTransport(http),
            StdioTransportConfig stdio => BuildStdioTransport(stdio),
            _ => throw new NotSupportedException(
                $"Unsupported transport config: {_transportConfig.GetType().Name}")
        };

        // Create the MCP client. The returned McpClient is IAsyncDisposable.
        _client = await McpClient.CreateAsync(
            transport,
            clientOptions: new McpClientOptions
            {
                // Give the client a recognizable identity.
                ClientInfo = new() { Name = "LearnMcpTutorial", Version = "1.0.0" }
            },
            cancellationToken: cancellationToken);

        // --- Rule 3: Handle listChanged notifications ---
        // When the server adds, removes, or updates tools, it sends a
        // notifications/tools/list_changed push.  We refresh our cache
        // automatically so the agent always uses the current schema.
        _client.RegisterNotificationHandler(
            "notifications/tools/list_changed",
            async (_, ct) =>
            {
                _logger.LogInformation("Received listChanged notification — refreshing tool cache");
                await RefreshToolsAsync(ct);
            });

        // --- Rule 1: Discover tools dynamically at runtime ---
        await RefreshToolsAsync(cancellationToken);

        _connected = true;
        _logger.LogInformation(
            "Successfully connected via {Transport} and discovered {Count} tools",
            _transportConfig.DisplayName, Tools.Count);
    }

    /// <summary>
    /// Builds a Streamable HTTP transport for a remote MCP server.
    /// </summary>
    private IClientTransport BuildHttpTransport(HttpTransportConfig config)
    {
        var url = config.Url;

        // Append maxTokenBudget query parameter if configured.
        // This caps the token count in search responses to stay within budget.
        if (config.MaxTokenBudget.HasValue)
        {
            var separator = url.Contains('?') ? '&' : '?';
            url = $"{url}{separator}maxTokenBudget={config.MaxTokenBudget.Value}";
        }

        _logger.LogInformation("Connecting to MCP server at {Url} (transport: Streamable HTTP)", url);

        // StreamableHttp is the current transport; AutoDetect is also safe
        // (tries Streamable HTTP first, falls back to legacy SSE).
        return new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(url),
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(30)
        });
    }

    /// <summary>
    /// Builds a stdio transport that launches a local MCP server as a child process.
    /// </summary>
    private IClientTransport BuildStdioTransport(StdioTransportConfig config)
    {
        _logger.LogInformation(
            "Launching local MCP server '{Label}' (transport: stdio): {Command} {Args}",
            config.Label, config.Command, string.Join(' ', config.Arguments));

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = config.Label,
            Command = config.Command,
            Arguments = [.. config.Arguments]
        });
    }

    /// <summary>
    /// Refreshes the tool cache by calling tools/list.
    /// Called on initial connection, on listChanged notification, and on stale-schema retry.
    /// </summary>
    public async Task RefreshToolsAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null)
            throw new InvalidOperationException("Client not connected. Call ConnectAsync first.");

        var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken);

        lock (_lock)
        {
            _tools = tools;
        }

        _logger.LogInformation(
            "Tool cache refreshed. {Count} tool(s) discovered: {Names}",
            tools.Count,
            string.Join(", ", tools.Select(t => t.Name)));

        foreach (var tool in tools)
        {
            _logger.LogDebug("  • {ToolName} — {Description}", tool.Name, tool.Description);
        }
    }

    /// <summary>
    /// Attempts to refresh tools and retry a failed operation when a stale-schema error
    /// is suspected. Implements Microsoft's Rule 2.
    /// </summary>
    /// <remarks>
    /// Because tool calls are orchestrated by Microsoft.Extensions.AI's function invocation
    /// middleware, this method is called by the caller when it detects a schema-related
    /// failure in the tool call results. After refreshing, the caller should retry
    /// the entire conversation turn.
    /// </remarks>
    public async Task<bool> TryRefreshAndRetryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogWarning("Stale schema suspected — refreshing tool cache and will retry");
            await RefreshToolsAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh tool cache on retry");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        _connected = false;
    }
}
