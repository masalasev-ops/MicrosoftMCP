using System.ClientModel;
using LearnMcpTutorial.Agent;
using LearnMcpTutorial.Diagnostics;
using LearnMcpTutorial.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
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
// Configuration comes from two JSON files at the repo root, both optional and
// both copied next to the binary at build time:
//
//   appsettings.json         committed defaults, no secrets
//   appsettings.Local.json   gitignored, your real key — overrides the above
//
// Environment variables are NOT read. (Earlier versions of this CLI honored
// LLM_PROVIDER / OPENAI_API_KEY / DEEPSEEK_API_KEY / OLLAMA_MODEL; those are
// gone. Put the same values under Llm:Provider, OpenAI:ApiKey, DeepSeek:ApiKey
// and Ollama:ModelId instead.)
// ─────────────────────────────────────────────────────────────────────────

var argList = args.ToList();
bool useLocal = argList.Remove("--local");
bool listOnly = argList.Remove("--list");
var question = string.Join(' ', argList).Trim();

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .Build();

using var loggerFactory = LoggerFactory.Create(b => b
    .AddConsole()
    .SetMinimumLevel(LogLevel.Warning));

// ── STEP 2: connect + discover tools (transport-agnostic) ──
McpTransportConfig transport = useLocal
    ? new StdioTransportConfig(
        Command: "dotnet",
        Arguments: ["run", "--project", ResolveServerPath(), "--no-build"],
        Label: "LearnMcpTutorial.Server")
    : new HttpTransportConfig(
        Setting("Mcp:Url") ?? "https://learn.microsoft.com/api/mcp",
        config.GetValue<int?>("Mcp:MaxTokenBudget"));

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

// Reads a configuration value, treating "" (which is what appsettings.json
// ships for the ApiKey placeholders) the same as a missing key.
string? Setting(string key) => string.IsNullOrWhiteSpace(config[key]) ? null : config[key];

// Build the raw LLM client from configuration. Same three providers as the WPF
// app — all OpenAI-compatible via Microsoft.Extensions.AI.OpenAI.
//
// DeepSeek's legacy aliases deepseek-chat / deepseek-reasoner retire on
// 2026-07-24. The default below is the non-legacy deepseek-v4-flash, so nothing
// here needs to change when they go.
IChatClient BuildChatClient()
{
    var provider = (Setting("Llm:Provider") ?? "openai").ToLowerInvariant();
    return provider switch
    {
        "deepseek" => Chat(
            RequireKey("DeepSeek"),
            Setting("DeepSeek:ModelId") ?? "deepseek-v4-flash",
            Setting("DeepSeek:BaseUrl") ?? "https://api.deepseek.com/v1"),
        // Ollama runs locally and ignores the credential, but the OpenAI client
        // insists on a non-empty one.
        "ollama" => Chat(
            "ollama",
            Setting("Ollama:ModelId") ?? "llama3.2",
            Setting("Ollama:BaseUrl") ?? "http://localhost:11434/v1"),
        // appsettings.json intentionally has no OpenAI:BaseUrl, so the client
        // keeps its own default endpoint unless one is configured.
        _ => Chat(
            RequireKey("OpenAI"),
            Setting("OpenAI:ModelId") ?? "gpt-4o-mini",
            Setting("OpenAI:BaseUrl"))
    };
}

static IChatClient Chat(string apiKey, string modelId, string? baseUrl) =>
    (baseUrl is null
        ? new OpenAIClient(new ApiKeyCredential(apiKey))
        : new OpenAIClient(new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }))
    .GetChatClient(modelId).AsIChatClient();

string RequireKey(string section) =>
    Setting($"{section}:ApiKey")
        ?? throw new InvalidOperationException(
            // $$ so that the JSON braces below are literal and {{section}} interpolates.
            $$"""
              No API key configured for provider '{{section}}'.

              Add it to appsettings.Local.json at the repo root:

                  { "{{section}}": { "ApiKey": "sk-..." } }

              Copy appsettings.Local.json.example to get started. That file is
              gitignored, so your key is never committed.

              Alternatives: pass --list to skip the LLM entirely, or set
              Llm:Provider to "ollama" to run a local model with no key.
              """);

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
