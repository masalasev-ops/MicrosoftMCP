using System.ClientModel;
using LearnMcpTutorial.Agent;
using LearnMcpTutorial.Diagnostics;
using LearnMcpTutorial.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;

// ─────────────────────────────────────────────────────────────────────────
// LearnMcpTutorial.Cli — a tiny, headless MCP client.
//
// This is the "graduation" of tutorial steps 2–5 (see docs/TUTORIAL.md):
//   • connects to an MCP server (remote Learn over HTTP, or the local stdio
//     server with --local),
//   • discovers tools at runtime,
//   • and — given a question — runs the SAME DocsAgent the WPF app uses.
//
// Usage:
//   dotnet run --project src/LearnMcpTutorial.Cli -- --list
//   dotnet run --project src/LearnMcpTutorial.Cli -- --local --list
//   dotnet run --project src/LearnMcpTutorial.Cli -- "How do I create an Azure Container App?"
//   dotnet run --project src/LearnMcpTutorial.Cli -- --local "What does HTTP 404 mean?"
//
// Provider config comes from environment variables:
//   LLM_PROVIDER = openai | deepseek | ollama   (default: openai)
//   OPENAI_API_KEY / DEEPSEEK_API_KEY           (not needed for ollama)
// ─────────────────────────────────────────────────────────────────────────

var argList = args.ToList();
bool useLocal = argList.Remove("--local");
bool listOnly = argList.Remove("--list");
var question = string.Join(' ', argList).Trim();

using var loggerFactory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(LogLevel.Warning));

// ── STEP 2: connect + discover tools (transport-agnostic) ──
McpTransportConfig transport = useLocal
    ? new StdioTransportConfig(
        Command: "dotnet",
        Arguments: ["run", "--project", ResolveServerPath(), "--no-build"],
        Label: "LearnMcpTutorial.Server")
    : new HttpTransportConfig("https://learn.microsoft.com/api/mcp");

await using var client = new LearnMcpClient(loggerFactory.CreateLogger<LearnMcpClient>(), transport);

Console.WriteLine($"Connecting via {transport.DisplayName}...");
await client.ConnectAsync();

Console.WriteLine($"\nDiscovered {client.Tools.Count} tool(s):");
foreach (var t in client.Tools)
    Console.WriteLine($"  • {t.Name} — {Trim(t.Description, 70)}");

if (listOnly || string.IsNullOrWhiteSpace(question))
{
    if (string.IsNullOrWhiteSpace(question) && !listOnly)
        Console.WriteLine("\n(no question given — pass one as an argument, or use --list)");
    return;
}

// ── STEPS 3–4: run the agent (LLM loop + live tracing) ──
var chatClient = BuildChatClient();
var agent = new DocsAgent(
    chatClient, client, loggerFactory.CreateLogger<DocsAgent>(),
    maxToolIterations: 10,
    onToolStep: step =>
    {
        foreach (var tc in step.ToolCalls)
            Console.WriteLine($"  🔧 {tc.FunctionName}({Trim(tc.FunctionArguments, 80)})");
        if (step.Type == TraceStepType.TextProduced && !string.IsNullOrWhiteSpace(step.Text))
            Console.WriteLine($"  💬 {Trim(step.Text, 100)}");
    });

Console.WriteLine($"\nAsking: {question}\n");
var result = await agent.AskAsync(question, client.Tools);

Console.WriteLine("\n─── Answer ───────────────────────────────────────────");
Console.WriteLine(result.Answer);
if (result.CitedUrls.Count > 0)
{
    Console.WriteLine("\n─── Sources ──────────────────────────────────────────");
    int i = 1;
    foreach (var url in result.CitedUrls) Console.WriteLine($"  {i++}. {url}");
}

// ── Helpers ──────────────────────────────────────────────────────────────

// Build the raw LLM client from environment config. Same three providers as
// the WPF app — all OpenAI-compatible via Microsoft.Extensions.AI.OpenAI.
IChatClient BuildChatClient()
{
    var provider = (Environment.GetEnvironmentVariable("LLM_PROVIDER") ?? "openai").ToLowerInvariant();
    return provider switch
    {
        "deepseek" => new OpenAIClient(
                new ApiKeyCredential(RequireKey("DEEPSEEK_API_KEY")),
                new OpenAIClientOptions { Endpoint = new Uri("https://api.deepseek.com/v1") })
            .GetChatClient("deepseek-v4-flash").AsIChatClient(),
        "ollama" => new OpenAIClient(
                new ApiKeyCredential("ollama"),
                new OpenAIClientOptions { Endpoint = new Uri("http://localhost:11434/v1") })
            .GetChatClient(Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "llama3.2").AsIChatClient(),
        _ => new OpenAIClient(RequireKey("OPENAI_API_KEY"))
            .GetChatClient("gpt-4o-mini").AsIChatClient()
    };
}

static string RequireKey(string envVar) =>
    Environment.GetEnvironmentVariable(envVar)
        ?? throw new InvalidOperationException(
            $"Set {envVar} (or use --list to skip the LLM, or LLM_PROVIDER=ollama for no key).");

// Walk up from the CLI's base dir to the repo root (MicrosoftMCP.slnx),
// then point at the server project so --local can launch it over stdio.
static string ResolveServerPath()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MicrosoftMCP.slnx")))
        dir = dir.Parent;
    return dir is not null
        ? Path.GetFullPath(Path.Combine(dir.FullName, "src/LearnMcpTutorial.Server"))
        : "src/LearnMcpTutorial.Server";
}

static string Trim(string? s, int max) =>
    string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..(max - 1)] + "…";
