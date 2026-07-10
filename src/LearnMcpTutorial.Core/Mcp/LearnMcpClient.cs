using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
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

    /// <summary>How long to wait for a connection when the caller doesn't say.</summary>
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Connects to the configured MCP server and discovers available tools.
    /// </summary>
    /// <param name="cancellationToken">Cancels the connect attempt.</param>
    /// <param name="connectTimeout">
    /// Deadline for the whole connect + discover sequence. Defaults to
    /// <see cref="DefaultConnectTimeout"/>. Without this the stdio path had no
    /// deadline at all: a server that starts but never completes the handshake
    /// would hang forever.
    /// </param>
    /// <exception cref="McpConnectionException">
    /// The server could not be reached. Carries transport-specific guidance and
    /// the original failure as <see cref="Exception.InnerException"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled by the caller.
    /// </exception>
    public async Task ConnectAsync(
        CancellationToken cancellationToken = default,
        TimeSpan? connectTimeout = null)
    {
        var timeout = connectTimeout ?? DefaultConnectTimeout;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        // ─────────────────────────────────────────────────────────────────
        // THE ONLY THING THAT DIFFERS BETWEEN SERVERS: the transport.
        // Build an IClientTransport from the config, then everything below
        // is identical whether we're talking to the remote Learn server or
        // a local stdio server.
        // ─────────────────────────────────────────────────────────────────
        IClientTransport transport;
        try
        {
            transport = _transportConfig switch
            {
                HttpTransportConfig http => BuildHttpTransport(http, timeout),
                StdioTransportConfig stdio => BuildStdioTransport(stdio),
                _ => throw new NotSupportedException(
                    $"Unsupported transport config: {_transportConfig.GetType().Name}")
            };
        }
        catch (UriFormatException ex)
        {
            throw new McpConnectionException(
                $"'{(_transportConfig as HttpTransportConfig)?.Url}' is not a valid MCP endpoint URL. " +
                "Check Mcp:Url in appsettings.Local.json.", ex);
        }

        try
        {
            // Create the MCP client. The returned McpClient is IAsyncDisposable.
            _client = await McpClient.CreateAsync(
                transport,
                clientOptions: new McpClientOptions
                {
                    // Give the client a recognizable identity.
                    ClientInfo = new() { Name = "LearnMcpTutorial", Version = "1.0.0" }
                },
                cancellationToken: linked.Token);

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
            await RefreshToolsAsync(linked.Token);
        }
        catch (Exception ex) when (ex is not McpConnectionException)
        {
            // A client created before the failure owns a child process (stdio) or
            // an HTTP session. Drop it, or --local leaves an orphaned `dotnet run`.
            await DisposeClientAsync();
            throw Translate(ex, cancellationToken, timeout);
        }

        _connected = true;
        _logger.LogInformation(
            "Successfully connected via {Transport} and discovered {Count} tools",
            _transportConfig.DisplayName, Tools.Count);
    }

    /// <summary>
    /// Turns a transport-level failure into an <see cref="McpConnectionException"/>
    /// whose message tells the user what to do about it.
    /// </summary>
    private Exception Translate(Exception ex, CancellationToken callerToken, TimeSpan timeout)
    {
        var target = _transportConfig switch
        {
            HttpTransportConfig http => http.Url,
            StdioTransportConfig stdio => $"{stdio.Command} {string.Join(' ', stdio.Arguments)}",
            _ => _transportConfig.DisplayName
        };

        return ex switch
        {
            // The caller asked to stop. That is not a connection failure.
            OperationCanceledException when callerToken.IsCancellationRequested => ex,

            OperationCanceledException => new McpConnectionException(
                $"Timed out after {timeout.TotalSeconds:0.#}s connecting to '{target}' via " +
                $"{_transportConfig.DisplayName}. The server may be unreachable, or slow to start.", ex),

            // Derives from IOException, so it must be tested before one.
            ClientTransportClosedException => new McpConnectionException(
                $"The MCP server exited before the handshake completed ({target}). " +
                "Run it directly to see its output — a build error or a crash on startup " +
                "is the usual cause.", ex),

            // Process could not be started: command not on PATH, or not executable.
            Win32Exception => new McpConnectionException(
                $"Could not start the MCP server process '{(_transportConfig as StdioTransportConfig)?.Command}'. " +
                "Check that the .NET SDK is installed and on your PATH.", ex),

            HttpRequestException => new McpConnectionException(
                $"Could not reach the MCP server at '{target}'. Check the URL, your network, " +
                "and whether the server is running.", ex),

            // McpProtocolException derives from McpException, so this covers both.
            McpException => new McpConnectionException(
                $"The MCP server at '{target}' responded, but the protocol handshake failed. " +
                "The server may speak a different MCP version.", ex),

            IOException => new McpConnectionException(
                $"Lost the connection to the MCP server ({target}) while connecting.", ex),

            _ => new McpConnectionException(
                $"Failed to connect to the MCP server via {_transportConfig.DisplayName} ({target}).", ex)
        };
    }

    private async Task DisposeClientAsync()
    {
        if (_client is null) return;
        try
        {
            await _client.DisposeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Ignoring failure while disposing a half-open MCP client");
        }
        _client = null;
    }

    /// <summary>
    /// Builds a Streamable HTTP transport for a remote MCP server.
    /// </summary>
    private IClientTransport BuildHttpTransport(HttpTransportConfig config, TimeSpan connectTimeout)
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
            ConnectionTimeout = connectTimeout
        });
    }

    /// <summary>
    /// Builds a stdio transport that launches a local MCP server as a child process.
    /// </summary>
    /// <remarks>
    /// The command and its arguments are checked before the process is spawned.
    /// A missing project directory or DLL otherwise surfaces as a child process
    /// that exits immediately, which the caller sees as a closed transport rather
    /// than as the obvious "that path doesn't exist".
    /// </remarks>
    private IClientTransport BuildStdioTransport(StdioTransportConfig config)
    {
        VerifyStdioTargetExists(config);

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
    /// Fails fast when the thing we are about to launch plainly isn't there.
    /// A bare command name (e.g. "dotnet") is left to PATH resolution, which
    /// surfaces as a <see cref="Win32Exception"/> and is translated separately.
    /// </summary>
    private static void VerifyStdioTargetExists(StdioTransportConfig config)
    {
        var command = config.Command;
        if (command.AsSpan().IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) >= 0
            && !File.Exists(command))
        {
            throw new McpConnectionException(
                $"The MCP server executable '{command}' does not exist.");
        }

        var args = config.Arguments;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "--project" && i + 1 < args.Count)
            {
                var project = args[i + 1];
                if (!Directory.Exists(project) && !File.Exists(project))
                {
                    throw new McpConnectionException(
                        $"The MCP server project '{project}' does not exist. " +
                        "Check LocalServer:ProjectPath in appsettings.Local.json.");
                }
            }
            else if (args[i].EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !File.Exists(args[i]))
            {
                throw new McpConnectionException(
                    $"The MCP server assembly '{args[i]}' does not exist. Build or publish it first.");
            }
        }
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
        await DisposeClientAsync();
        _connected = false;
    }
}
