using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using LearnMcpTutorial.Agent;
using LearnMcpTutorial.Diagnostics;
using LearnMcpTutorial.Mcp;
using LearnMcpTutorial.Wpf.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using OpenAI;

namespace LearnMcpTutorial.Wpf.ViewModels;

public sealed class MainViewModel : BaseViewModel, IAsyncDisposable
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration _config;
    private LearnMcpClient? _learnClient;
    private IChatClient? _cachedChatClient;
    private string? _lastProviderFingerprint;
    private CancellationTokenSource? _cts;
    private int? _maxTokenBudget;

    // Persisted agent for multi-turn conversation. Rebuilt when the provider
    // config or the connected client changes; reset by Clear / Connect.
    private DocsAgent? _docsAgent;
    private string? _agentFingerprint;

    // ── Animation queue ──
    private sealed record AnimationFrame(
        int PipelineStep, int SequenceStep,
        string? Component, string? Status, string? ToolName);

    private readonly ConcurrentQueue<AnimationFrame> _animQueue = new();
    private readonly SemaphoreSlim _animSignal = new(0);
    private CancellationTokenSource? _animCts;
    private Task? _animTask;

    public ObservableCollection<string> Providers { get; } = ["openai", "deepseek", "ollama"];

    private int _selectedProviderIndex;
    public int SelectedProviderIndex
    {
        get => _selectedProviderIndex;
        set
        {
            if (SetProperty(ref _selectedProviderIndex, value))
            {
                OnPropertyChanged(nameof(SelectedProvider));
                OnPropertyChanged(nameof(ShowBaseUrl));
                UpdateDefaultsForProvider();
            }
        }
    }

    public string SelectedProvider => SelectedProviderIndex switch { 0 => "openai", 1 => "deepseek", _ => "ollama" };
    public bool ShowBaseUrl => SelectedProvider is "deepseek" or "ollama";

    private string _modelId = "gpt-4o-mini";
    public string ModelId { get => _modelId; set => SetProperty(ref _modelId, value); }
    private string _apiKey = "";
    public string ApiKey { get => _apiKey; set => SetProperty(ref _apiKey, value); }
    private string _baseUrl = "";
    public string BaseUrl { get => _baseUrl; set => SetProperty(ref _baseUrl, value); }
    private string _mcpUrl = "https://learn.microsoft.com/api/mcp";
    public string McpUrl { get => _mcpUrl; set => SetProperty(ref _mcpUrl, value); }

    // ── Transport selection (remote Learn HTTP  ↔  local stdio server) ──
    public ObservableCollection<string> TransportModes { get; } =
        ["Learn MCP (remote)", "Local server (stdio)"];

    private int _selectedTransportIndex;
    public int SelectedTransportIndex
    {
        get => _selectedTransportIndex;
        set
        {
            if (SetProperty(ref _selectedTransportIndex, value))
            {
                OnPropertyChanged(nameof(IsLocalServer));
                OnPropertyChanged(nameof(ShowMcpUrl));
                OnPropertyChanged(nameof(ShowLocalServerPath));
            }
        }
    }

    public bool IsLocalServer => SelectedTransportIndex == 1;
    public bool ShowMcpUrl => !IsLocalServer;
    public bool ShowLocalServerPath => IsLocalServer;

    private string _localServerPath = "";
    public string LocalServerPath { get => _localServerPath; set => SetProperty(ref _localServerPath, value); }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            if (SetProperty(ref _isConnected, value))
            {
                OnPropertyChanged(nameof(CanAsk));
                ((RelayCommand)AskCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanAsk => IsConnected && !IsBusy;
    private string _statusText = "Not connected";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanAsk));
                ((RelayCommand)ConnectCommand).RaiseCanExecuteChanged();
                ((RelayCommand)AskCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<ToolInfo> DiscoveredTools { get; } = [];
    public ObservableCollection<string> Sources { get; } = [];
    public ObservableCollection<TraceEntry> ToolCallTrace { get; } = [];

    private string _question = "";
    public string Question { get => _question; set => SetProperty(ref _question, value); }
    private string _answer = "";
    public string Answer { get => _answer; set => SetProperty(ref _answer, value); }

    // ── Architecture diagram highlighting state ──
    private string _highlightedComponent = "";
    public string HighlightedComponent { get => _highlightedComponent; set => SetProperty(ref _highlightedComponent, value); }

    private string _architectureStatus = "Ready — click Connect to start";
    public string ArchitectureStatus { get => _architectureStatus; set => SetProperty(ref _architectureStatus, value); }

    private int _activePipelineStep;
    public int ActivePipelineStep { get => _activePipelineStep; set => SetProperty(ref _activePipelineStep, value); }

    private int _activeSequenceStep;
    public int ActiveSequenceStep { get => _activeSequenceStep; set => SetProperty(ref _activeSequenceStep, value); }

    private string _activeToolName = "";
    public string ActiveToolName { get => _activeToolName; set => SetProperty(ref _activeToolName, value); }

    private int _toolCallCount;

    public ICommand ConnectCommand { get; }
    public ICommand AskCommand { get; }
    public ICommand ClearCommand { get; }

    public MainViewModel(ILogger<MainViewModel> logger, ILoggerFactory loggerFactory, IConfiguration config)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config;
        ConnectCommand = new RelayCommand(ConnectAsync, () => !IsBusy);
        AskCommand = new RelayCommand(AskAsync, () => IsConnected && !IsBusy);
        ClearCommand = new RelayCommand(ClearAsync);
        LoadConfiguration();
        StartAnimationLoop();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Animation queue — spaces out highlight transitions so each step
    //  is visible (~700ms delay).  The queue is drained on a background
    //  thread; each frame is dispatched to the UI thread for binding.
    // ═══════════════════════════════════════════════════════════════════

    private void StartAnimationLoop()
    {
        _animCts = new CancellationTokenSource();
        _animTask = Task.Run(() => RunAnimationLoopAsync(_animCts.Token));
    }

    private async Task RunAnimationLoopAsync(CancellationToken ct)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _animSignal.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (_animQueue.TryDequeue(out var frame))
            {
                await d.InvokeAsync(() => ApplyFrame(frame));
                await Task.Delay(700, ct);
            }
        }

        // Drain any remaining frames on cancellation
        while (_animQueue.TryDequeue(out var frame))
        {
            await d.InvokeAsync(() => ApplyFrame(frame));
        }
    }

    private void EnqueueFrame(int pipelineStep, int sequenceStep,
        string? component = null, string? status = null, string? toolName = null)
    {
        _animQueue.Enqueue(new AnimationFrame(pipelineStep, sequenceStep, component, status, toolName));
        try { _animSignal.Release(); } catch (SemaphoreFullException) { }
    }

    private void ApplyFrame(AnimationFrame frame)
    {
        if (frame.PipelineStep >= 0) ActivePipelineStep = frame.PipelineStep;
        if (frame.SequenceStep >= 0) ActiveSequenceStep = frame.SequenceStep;
        if (frame.Component is not null) HighlightedComponent = frame.Component;
        if (frame.Status is not null) ArchitectureStatus = frame.Status;
        if (frame.ToolName is not null) ActiveToolName = frame.ToolName;
    }

    private async Task WaitForAnimationDrainAsync(TimeSpan timeout)
    {
        // Give the animation queue time to process remaining frames
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_animQueue.IsEmpty) break;
            await Task.Delay(200);
        }
        // One more delay so the last frame is shown before we overwrite
        await Task.Delay(800);
    }

    // ═══════════════════════════════════════════════════════════════════

    private void LoadConfiguration()
    {
        var provider = _config["Llm:Provider"] ?? "openai";
        SelectedProviderIndex = provider.ToLowerInvariant() switch { "deepseek" => 1, "ollama" => 2, _ => 0 };
        McpUrl = _config["Mcp:Url"] ?? "https://learn.microsoft.com/api/mcp";
        _maxTokenBudget = _config.GetValue<int?>("Mcp:MaxTokenBudget");
        LocalServerPath = ResolveLocalServerPath();
        UpdateDefaultsForProvider();
    }

    /// <summary>
    /// Resolves the local MCP server's project directory. Walks up from the
    /// app's base directory to find the repo root (marked by MicrosoftMCP.slnx),
    /// then appends the configured relative path.
    /// </summary>
    private string ResolveLocalServerPath()
    {
        var relative = _config["LocalServer:ProjectPath"] ?? "src/LearnMcpTutorial.Server";

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MicrosoftMCP.slnx")))
            dir = dir.Parent;

        // If we found the repo root, build an absolute path; otherwise fall
        // back to the relative path (the user can edit it in the UI).
        return dir is not null
            ? Path.GetFullPath(Path.Combine(dir.FullName, relative))
            : relative;
    }

    private void UpdateDefaultsForProvider()
    {
        var section = SelectedProvider switch { "openai" => "OpenAI", "deepseek" => "DeepSeek", _ => "Ollama" };
        var m = _config[$"{section}:ModelId"]; if (!string.IsNullOrWhiteSpace(m)) ModelId = m;
        var b = _config[$"{section}:BaseUrl"]; if (!string.IsNullOrWhiteSpace(b)) BaseUrl = b;
    }

    private async Task ConnectAsync()
    {
        IsBusy = true;
        var target = IsLocalServer ? "local stdio server" : "Microsoft Learn MCP Server";
        StatusText = $"Connecting to {target}...";
        EnqueueFrame(-1, -1, "WpfApp", $"Initiating connection to {target}...");
        try
        {
            if (_learnClient is not null) await _learnClient.DisposeAsync();

            // ─────────────────────────────────────────────────────────────
            // Build the transport config based on the selected mode.
            // This is the ONLY code that differs between the remote Learn
            // server and the local stdio server. DocsAgent, the tool loop,
            // tracing, and the UI are all completely unchanged.
            // ─────────────────────────────────────────────────────────────
            McpTransportConfig transport = IsLocalServer
                ? new StdioTransportConfig(
                    Command: "dotnet",
                    Arguments: ["run", "--project", LocalServerPath, "--no-build"],
                    Label: "LearnMcpTutorial.Server")
                : new HttpTransportConfig(McpUrl, _maxTokenBudget);

            _learnClient = new LearnMcpClient(_loggerFactory.CreateLogger<LearnMcpClient>(), transport);

            EnqueueFrame(-1, -1, "McpServer",
                IsLocalServer
                    ? "Launching local server and connecting over stdio..."
                    : "Connecting to learn.microsoft.com/api/mcp over Streamable HTTP...");
            await _learnClient.ConnectAsync();

            EnqueueFrame(-1, -1, "LearnDocs", "Discovering tools from MCP Server...");
            DiscoveredTools.Clear();
            foreach (var t in _learnClient.Tools)
                DiscoveredTools.Add(new ToolInfo(t.Name, t.Description ?? "(no description)"));
            IsConnected = true;
            StatusText = $"Connected ({_learnClient.TransportDescription}) - {DiscoveredTools.Count} tools discovered";

            // New connection → start a fresh multi-turn conversation.
            _docsAgent = null; _agentFingerprint = null;

            await WaitForAnimationDrainAsync(TimeSpan.FromSeconds(5));
            HighlightedComponent = ""; ActivePipelineStep = 0; ActiveSequenceStep = 0;
            ArchitectureStatus = $"Connected via {_learnClient.TransportDescription}! {DiscoveredTools.Count} tools ready. Type a question to begin.";
        }
        catch (Exception ex)
        {
            IsConnected = false; StatusText = $"Connection failed: {ex.Message}";
            await WaitForAnimationDrainAsync(TimeSpan.FromSeconds(1));
            HighlightedComponent = ""; ActivePipelineStep = 0; ActiveSequenceStep = 0;
            ArchitectureStatus = IsLocalServer
                ? "Connection failed — make sure the server project is built (build the solution)."
                : "Connection failed — check the MCP URL and try again.";
            _logger.LogError(ex, "MCP connect failed");
        }
        finally { IsBusy = false; }
    }

    private async Task AskAsync()
    {
        if (string.IsNullOrWhiteSpace(Question) || _learnClient is null || !IsConnected) return;
        IsBusy = true; StatusText = "Researching on Microsoft Learn...";
        ToolCallTrace.Clear(); Sources.Clear(); Answer = "";
        _toolCallCount = 0;

        // Step 1: user asked
        EnqueueFrame(1, 1, "", "DocsAgent: Building system prompt with grounding rules...");
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            // Reuse the persisted agent so follow-up questions have context (multi-turn).
            var agent = GetOrCreateAgent();

            // Stream the answer token-by-token into the bound Answer property.
            // We're on the UI thread here (the await foreach resumes on the WPF
            // dispatcher), so updating Answer live is safe.
            var sb = new StringBuilder();
            await foreach (var delta in agent.AskStreamingAsync(
                Question, _learnClient.Tools, cancellationToken: _cts.Token))
            {
                sb.Append(delta);
                Answer = sb.ToString();
            }

            // Extract cited sources from the completed answer text.
            var citedUrls = DocsAgent.ExtractCitedUrls(sb.ToString());
            int idx = 1;
            foreach (var url in citedUrls) Sources.Add($"{idx++}. {url}");
            StatusText = $"Done (turn {agent.TurnCount}) - {citedUrls.Count} sources cited";

            // Let the animation queue drain so step highlights are visible
            await WaitForAnimationDrainAsync(TimeSpan.FromSeconds(8));

            ActivePipelineStep = 0; ActiveSequenceStep = 8;
            ArchitectureStatus = agent.TurnCount > 1
                ? $"Done! Follow-up answered (turn {agent.TurnCount}) with {citedUrls.Count} cited sources — ask another to continue the conversation."
                : $"Done! Answer synthesized with {citedUrls.Count} cited sources from Microsoft Learn.";
            HighlightedComponent = "";
        }
        catch (OperationCanceledException)
        {
            _docsAgent = null; _agentFingerprint = null; // reset broken conversation
            StatusText = "Cancelled";
            await WaitForAnimationDrainAsync(TimeSpan.FromSeconds(1));
            ArchitectureStatus = "Operation cancelled."; ActivePipelineStep = 0; ActiveSequenceStep = 0;
        }
        catch (AgentException ex)
        {
            _docsAgent = null; _agentFingerprint = null; // reset broken conversation
            StatusText = $"Agent error: {ex.Message}";
            await WaitForAnimationDrainAsync(TimeSpan.FromSeconds(1));
            ArchitectureStatus = $"Error: {ex.Message}"; ActivePipelineStep = 0; ActiveSequenceStep = 0;
        }
        catch (Exception ex)
        {
            _docsAgent = null; _agentFingerprint = null; // reset broken conversation
            StatusText = $"Error: {ex.Message}";
            await WaitForAnimationDrainAsync(TimeSpan.FromSeconds(1));
            ArchitectureStatus = $"Error: {ex.Message}"; ActivePipelineStep = 0; ActiveSequenceStep = 0;
            _logger.LogError(ex, "Ask failed");
        }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    /// <summary>
    /// Returns the persisted <see cref="DocsAgent"/>, rebuilding it if the
    /// provider configuration changed. Reusing the agent preserves its
    /// conversation history so follow-up questions have context (multi-turn).
    /// </summary>
    private DocsAgent GetOrCreateAgent()
    {
        // Same fingerprint basis as BuildChatClient, so the two never disagree
        // about whether the provider configuration changed.
        var fp = $"{SelectedProvider}|{ModelId}|{EffectiveApiKey()}|{BaseUrl}";
        if (_docsAgent is null || _agentFingerprint != fp)
        {
            _docsAgent = new DocsAgent(BuildChatClient(), _learnClient!,
                _loggerFactory.CreateLogger<DocsAgent>(), 10, OnToolStep);
            _agentFingerprint = fp;
        }
        return _docsAgent;
    }

    private void OnToolStep(TraceStep step)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) ProcessTraceStep(step);
        else d.InvokeAsync(() => ProcessTraceStep(step));
    }

    private void ProcessTraceStep(TraceStep step)
    {
        switch (step.Type)
        {
            case TraceStepType.ToolCallsRequested:
                _toolCallCount++;
                foreach (var tc in step.ToolCalls)
                {
                    ToolCallTrace.Add(new TraceEntry
                    {
                        Icon = "🔧",
                        Message = $"{tc.FunctionName}({Truncate(tc.FunctionArguments, 120)})"
                    });

                    // Enqueue animation frames for pipeline AND sequence
                    var toolName = tc.FunctionName;

                    if (toolName.Contains("search", StringComparison.OrdinalIgnoreCase))
                    {
                        // Frame 1: tool call requested
                        EnqueueFrame(2, 2, "McpServer",
                            $"LLM → {toolName}(...) — searching Microsoft Learn...", toolName);
                    }
                    else if (toolName.Contains("fetch", StringComparison.OrdinalIgnoreCase))
                    {
                        EnqueueFrame(2, 4, "McpServer",
                            $"LLM → {toolName}(url) — fetching full documentation page...", toolName);
                    }
                    else if (toolName.Contains("code", StringComparison.OrdinalIgnoreCase))
                    {
                        EnqueueFrame(2, 6, "McpServer",
                            $"LLM → {toolName}(...) — looking up code samples...", toolName);
                    }
                }
                // Tracing layer intercepting
                EnqueueFrame(3, -1, "", null);
                break;

            case TraceStepType.TextProduced:
                if (!string.IsNullOrWhiteSpace(step.Text))
                {
                    ToolCallTrace.Add(new TraceEntry
                    {
                        Icon = "💬",
                        Message = $"LLM: {Truncate(step.Text, 200)}"
                    });

                    // Show LLM provider responding
                    EnqueueFrame(4, -1, "LlmProvider", null);

                    // Enqueue the result step based on what was happening
                    if (_toolCallCount == 0)
                    {
                        EnqueueFrame(-1, 8, "", "LLM synthesizing final answer (no tools needed)...");
                    }
                    else if (_activeSequenceStep == 2 || _activeSequenceStep == 3)
                    {
                        EnqueueFrame(-1, 3, "", "MCP Server returned search results — LLM reviewing...");
                    }
                    else if (_activeSequenceStep == 4 || _activeSequenceStep == 5)
                    {
                        EnqueueFrame(-1, 5, "", "MCP Server returned full documentation page — LLM verifying details...");
                    }
                    else if (_activeSequenceStep == 6 || _activeSequenceStep == 7)
                    {
                        EnqueueFrame(-1, 7, "", "MCP Server returned code samples — LLM verifying syntax...");
                    }
                }
                break;
        }
    }

    /// <summary>
    /// The key typed into the password box, falling back to
    /// <c>{Provider}:ApiKey</c> from appsettings.Local.json when the box is
    /// empty. The box always wins when it has content.
    /// </summary>
    private string EffectiveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey)) return ApiKey;
        var section = SelectedProvider switch { "openai" => "OpenAI", "deepseek" => "DeepSeek", _ => "Ollama" };
        return _config[$"{section}:ApiKey"] ?? "";
    }

    private IChatClient BuildChatClient()
    {
        // Fingerprint on the effective key, not the raw property, so that a key
        // arriving from configuration still busts the cache.
        var apiKey = EffectiveApiKey();
        var fp = $"{SelectedProvider}|{ModelId}|{apiKey}|{BaseUrl}";
        if (_cachedChatClient is not null && _lastProviderFingerprint == fp) return _cachedChatClient;
        _cachedChatClient?.Dispose();
        _cachedChatClient = SelectedProvider switch
        {
            "openai" => new OpenAIClient(apiKey).GetChatClient(ModelId).AsIChatClient(),
            "deepseek" => new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(string.IsNullOrWhiteSpace(BaseUrl) ? "https://api.deepseek.com/v1" : BaseUrl) })
                .GetChatClient(ModelId).AsIChatClient(),
            _ => new OpenAIClient(new System.ClientModel.ApiKeyCredential("ollama"),
                new OpenAIClientOptions { Endpoint = new Uri(string.IsNullOrWhiteSpace(BaseUrl) ? "http://localhost:11434/v1" : BaseUrl) })
                .GetChatClient(ModelId).AsIChatClient()
        };
        _lastProviderFingerprint = fp; return _cachedChatClient;
    }

    private Task ClearAsync()
    {
        Question = ""; Answer = ""; ToolCallTrace.Clear(); Sources.Clear();
        // Clear multi-turn history so the next question starts fresh.
        _docsAgent?.ResetConversation();
        // Drain any queued animation frames
        while (_animQueue.TryDequeue(out _)) { }
        HighlightedComponent = ""; ActivePipelineStep = 0; ActiveSequenceStep = 0;
        ActiveToolName = ""; _toolCallCount = 0;
        ArchitectureStatus = IsConnected ? "Ready — type a question to begin" : "Ready — click Connect to start";
        StatusText = IsConnected ? $"Connected - {DiscoveredTools.Count} tools" : "Not connected";
        return Task.CompletedTask;
    }

    private static string Truncate(string t, int max) => t.Length <= max ? t : t[..(max - 3)] + "...";

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel(); _cts?.Dispose();
        _animCts?.Cancel();
        try { _animSignal.Release(); } catch (SemaphoreFullException) { }
        if (_animTask is not null)
        {
            try { await _animTask; } catch (OperationCanceledException) { }
        }
        _animCts?.Dispose(); _animSignal.Dispose();
        _cachedChatClient?.Dispose();
        if (_learnClient is not null) await _learnClient.DisposeAsync();
    }
}
