# TUTORIAL — Learn MCP in 6 Runnable Steps

This is a staged learning path. Each step is small, runnable, and builds on the
last — climb from "poke a server with no code" all the way to the full WPF app.

Steps 3–6 point at **real, shipped code in this repo** — you don't have to check
anything out. Step 2 is a self-contained snippet you can paste into a scratch
console app.

| Step | You learn | Where the code lives | Run it |
|---|---|---|---|
| 1 | What a server exposes (no code) | — | MCP Inspector |
| 2 | Connect + discover tools | snippet below (≈12 lines) | paste into a scratch console app |
| 3 | Add the LLM tool-calling loop | [src/LearnMcpTutorial.Cli/](../src/LearnMcpTutorial.Cli/) + [DocsAgent](../src/LearnMcpTutorial.Core/Agent/DocsAgent.cs) | `dotnet run --project src/LearnMcpTutorial.Cli -- "<question>"` |
| 4 | Add live tool-call tracing | [ToolCallTracingChatClient](../src/LearnMcpTutorial.Core/Diagnostics/ToolCallTracingChatClient.cs) | same CLI — watch the 🔧/💬 trace |
| 5 | Author your own server + point the client at it | [src/LearnMcpTutorial.Server/](../src/LearnMcpTutorial.Server/) | `dotnet run --project src/LearnMcpTutorial.Cli -- --local --list` |
| 6 | The full GUI experience | [src/LearnMcpTutorial.Wpf/](../src/LearnMcpTutorial.Wpf/) | `dotnet run --project src/LearnMcpTutorial.Wpf` |

---

## Step 1 — Poke the server with MCP Inspector (no code)

Before writing anything, *see* what an MCP server offers. [MCP Inspector](https://github.com/modelcontextprotocol/inspector)
is a free Node tool that connects to any MCP server and lets you browse it.

```bash
npx @modelcontextprotocol/inspector
# → Transport: Streamable HTTP
# → URL: https://learn.microsoft.com/api/mcp
# → Click "List Tools", then invoke microsoft_docs_search
#   with query: "Azure Container App managed identity"
```

**Takeaway:** a server advertises **tools** (and optionally prompts and
resources) that a client discovers at runtime. Nothing is hardcoded.

---

## Step 2 — Your first client: connect + list tools

The smallest possible MCP client — about a dozen lines, one package
(`ModelContextProtocol`), no LLM. It connects to the Learn server and prints its
tools. To run it, paste it into a fresh console app (`dotnet new console` then
`dotnet add package ModelContextProtocol`).

**The whole program:**

```csharp
using ModelContextProtocol.Client;

var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("https://learn.microsoft.com/api/mcp"),
    TransportMode = HttpTransportMode.StreamableHttp
});

await using var client = await McpClient.CreateAsync(transport);   // no auth
var tools = await client.ListToolsAsync();                          // discover!

foreach (var tool in tools)
    Console.WriteLine($"• {tool.Name}: {tool.Description}");
```

**Takeaway:** three calls — build transport, create client, `ListToolsAsync()`.
Each returned `McpClientTool` is also a `Microsoft.Extensions.AI.AIFunction`,
which is exactly what the next step needs.

---

## Step 3 — Add the LLM: automatic tool calling

Now let an LLM decide which tools to call. The key insight:
`Microsoft.Extensions.AI` runs the tool-calling loop **for you** — you never
write a `while` loop.

This is where the checked-in CLI ([src/LearnMcpTutorial.Cli/](../src/LearnMcpTutorial.Cli/))
comes in. It wraps the raw LLM client with function invocation and passes the
discovered tools as `ChatOptions.Tools` (see [DocsAgent.cs](../src/LearnMcpTutorial.Core/Agent/DocsAgent.cs)):

```csharp
_chatClient = innerChatClient          // OpenAI / DeepSeek / Ollama
    .AsBuilder()
    .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = 10)
    .Build();

var options = new ChatOptions { Tools = [.. tools] };   // MCP tools plug in directly
var response = await _chatClient.GetResponseAsync(messages, options);
```

**Run it** (needs an API key — or use Ollama, see the README):

```bash
# Put your key in appsettings.Local.json at the repo root first:
#   { "OpenAI": { "ApiKey": "sk-..." } }
# Copy appsettings.Local.json.example to get started. Environment variables
# such as OPENAI_API_KEY are not read.
dotnet run --project src/LearnMcpTutorial.Cli -- "How do I create an Azure Container App with managed identity?"
```

The LLM will call `microsoft_docs_search`, then `microsoft_docs_fetch`, then
synthesize a grounded answer with cited `learn.microsoft.com` URLs.

**Takeaway:** because `McpClientTool : AIFunction`, MCP tools drop straight into
the M.E.AI pipeline. The `FunctionInvokingChatClient` middleware executes each
requested tool and feeds the result back — no orchestration code.

---

## Step 4 — See inside the loop: tracing middleware

How do you *watch* the tool calls happen? Insert an `IChatClient` wrapper
between the function-invocation middleware and the raw LLM. It intercepts every
round-trip and fires a callback.

[ToolCallTracingChatClient.cs](../src/LearnMcpTutorial.Core/Diagnostics/ToolCallTracingChatClient.cs)
is that wrapper; the CLI subscribes to it and prints a live trace:

```csharp
var agent = new DocsAgent(chatClient, client, logger, maxToolIterations: 10,
    onToolStep: step =>
    {
        foreach (var tc in step.ToolCalls)
            Console.WriteLine($"🔧 {tc.FunctionName}(...)");   // tool requested
        if (step.Type == TraceStepType.TextProduced)
            Console.WriteLine($"💬 {step.Text}");              // LLM text
    });
```

**Run it** — the same command as Step 3 now shows a 🔧/💬 trace as it works.
(The WPF app in Step 6 turns this same callback into a live-animated panel.)

**Takeaway:** middleware composition (`ChatClientBuilder.Use(...)`) is how you
observe or modify the pipeline without touching the loop itself.

---

## Step 5 — Author your own server, then point the client at it

So far you've *consumed* a server. Now *author* one. [src/LearnMcpTutorial.Server/](../src/LearnMcpTutorial.Server/)
is a stdio MCP server exposing three tools, one prompt, and one resource
(all three MCP primitives) — see [Program.cs](../src/LearnMcpTutorial.Server/Program.cs):

```csharp
options.ToolCollection.Add(McpServerTool.Create(
    (int code) => LookUpHttpStatus(code),
    new McpServerToolCreateOptions { Name = "http_status", ReadOnly = true, /* ... */ }));

var transport = new StdioServerTransport(options, loggerFactory);
await McpServer.Create(transport, options, loggerFactory, null).RunAsync();
```

The magic: the **same** `LearnMcpClient` and `DocsAgent` talk to it. Only the
transport line changes (stdio instead of HTTP):

```bash
# List the LOCAL server's tools (no LLM, no key needed):
dotnet run --project src/LearnMcpTutorial.Cli -- --local --list

# Or ask a question the local tools can answer:
dotnet run --project src/LearnMcpTutorial.Cli -- --local "What does HTTP 404 mean?"
```

`--local` finds the server through
[LocalServerLocator](../src/LearnMcpTutorial.Core/Mcp/LocalServerLocator.cs): first the
project directory from `LocalServer:ProjectPath` (relative paths resolve against the
repo root), then a published `LearnMcpTutorial.Server.dll` sitting next to the CLI.
That second probe only fires if you publish the CLI **and** the server into the same
folder — an ordinary build does not copy the server's assembly next to the CLI, since
the reference is declared `ReferenceOutputAssembly="false"`. If neither is found the
CLI names every path it tried instead of hanging.

**Takeaway:** MCP clients are **server-agnostic**. Swapping remote↔local changes
one `switch` arm in [LearnMcpClient.ConnectAsync](../src/LearnMcpTutorial.Core/Mcp/LearnMcpClient.cs);
the agent, tool loop, and tracing are untouched. See the README's
[§4 Core Concepts](../README.md#4-mcp-core-concepts-primitives-transports--auth)
for primitives, transports, and auth.

---

## Step 6 — The destination: the full WPF GUI

Everything you built now has a face. [src/LearnMcpTutorial.Wpf/](../src/LearnMcpTutorial.Wpf/)
wraps the identical Core library in a desktop app with:

- a **Server** dropdown to switch remote↔local (Step 5, in the UI),
- a **Provider** dropdown (OpenAI / DeepSeek / Ollama / LM Studio),
- a **live Tool Call Trace** panel (Step 4's callback, animated),
- **interactive architecture diagrams** that highlight each pipeline stage and
  sequence step in real time,
- and **IDE-style markdown rendering** for code in answers.

**Run it** (Windows only — WPF targets `net10.0-windows`):

```bash
dotnet run --project src/LearnMcpTutorial.Wpf
```

Then: pick a provider, enter your key, click **Connect**, ask a question, and
switch to the **Architecture** tab to watch the whole flow light up.

**Takeaway:** the GUI adds zero new MCP concepts — it's the same client code from
Steps 2–5 with a richer presentation layer. Once you understand the steps, the
app holds no mysteries.

---

## Where to go next

- **[README](../README.md)** — full reference: concepts, security model, provider
  config, troubleshooting.
- **[DEMO.md](../DEMO.md)** — a guided walkthrough of the WPF app.
- **Extend it** — add your own tool to the local server, then watch the CLI and
  WPF discover it automatically (dynamic discovery in action).
