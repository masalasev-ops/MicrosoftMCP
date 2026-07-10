# TUTORIAL — Learn MCP in 6 Runnable Steps

This is a staged learning path. Each step is small, runnable, and builds on the
last — climb from "poke a server with no code" all the way to the full WPF app.

Steps 3–6 point at **real, shipped code in this repo** — you don't have to check
anything out. Steps 2 and 3a are self-contained snippets you can paste into a
scratch console app.

## Before you start

This assumes you can run `dotnet` from a terminal and read basic C# (`async`/`await`,
`var`, collection expressions). You do **not** need prior MCP, LLM-API, or WPF
experience — that's what the steps build. Budget **~30 minutes** end to end; Steps 1–2
are free and keyless, Step 3 onward wants a provider (use **Ollama** to stay free —
see the README's [§8](../README.md#8-running-for-free-with-ollama)).

**Three words this tutorial uses precisely:**

| Term       | In this repo                                                              |
| ---------- | ------------------------------------------------------------------------ |
| **Server** | Exposes tools/prompts/resources. Learn's HTTP endpoint, or your local stdio exe. |
| **Client** | Connects to one server, discovers what it offers, invokes it. `LearnMcpClient`. |
| **Host**   | The application that owns the client(s) and wires in the LLM. The CLI and the WPF app are hosts. |

A host contains a client; a client talks to a server. Steps 2–5 build the client;
Steps 3–6 grow the host around it.

> The README's §1 calls the WPF app a "host" — same meaning as above. Throughout this
> tutorial, "client" is the object that speaks MCP; "host" is the app around it.

| Step | You learn | Where the code lives | Run it |
|---|---|---|---|
| 1 | What a server exposes (no code) | — | MCP Inspector |
| 2 | Connect + discover tools | snippet below (≈12 lines) | paste into a scratch console app |
| 3 | Add the LLM tool-calling loop | [src/LearnMcpTutorial.Cli/](../src/LearnMcpTutorial.Cli/) + [DocsAgent](../src/LearnMcpTutorial.Core/Agent/DocsAgent.cs) | `dotnet run --project src/LearnMcpTutorial.Cli -- "<question>"` |
| 3a | The tool-calling loop by hand (optional) | snippet below | paste into a scratch console app |
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

`You are here:  [Client] ──▶ [Server]        (discover tools, no LLM)`

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

**Expected output** (tool descriptions abbreviated):

```
• microsoft_docs_search: Search official Microsoft/Azure documentation to find the most relevant…
• microsoft_code_sample_search: Search for code snippets and examples in official Microsoft Learn docs…
• microsoft_docs_fetch: Fetch and convert a Microsoft Learn documentation webpage to markdown…
```

Three tools, discovered at runtime — you hardcoded none of them. If you see them, the
handshake worked.

**Try it yourself:** point `Endpoint` at your own favourite MCP server instead, rerun,
and watch a completely different tool list appear. Same three calls, zero code changes.

**Takeaway:** three calls — build transport, create client, `ListToolsAsync()`.
Each returned `McpClientTool` is also a `Microsoft.Extensions.AI.AIFunction`,
which is exactly what the next step needs.

---

## Step 3 — Add the LLM: automatic tool calling

`You are here:  [Client] ──▶ [Server] + [LLM loop]   (LLM picks tools)`

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

**Expected output** (a real run — LM Studio `qwen3.5-9b`; your model, tool calls, and phrasing will differ):

```
🔧 microsoft_docs_search({"query":"Azure Container App create managed identity"})
💬 Based on the Microsoft documentation, I'll explain how to create an Azure Container App with managed identity…

─── Answer ───
… a grounded, step-by-step answer with `az containerapp identity` commands …

─── Sources ───
  1. https://learn.microsoft.com/azure/container-apps/managed-identity#configure-managed-identities
  2. https://learn.microsoft.com/azure/container-apps/managed-identity
  3. https://learn.microsoft.com/azure/container-apps/tutorial-code-to-cloud#create-a-user-assigned-managed-identity
```

If the answer cites a real `learn.microsoft.com` URL, the grounding worked. If it
answers with **no** tool calls and no sources, your model likely ignored the tools —
try a stronger model (see the Ollama quality note in the README).

**Try it yourself:** set `MaximumIterationsPerRequest = 1` and ask the same question.
The model gets exactly one tool round-trip, so it often answers before it has fetched
enough — a concrete demonstration of what the iteration bound protects against.

**Takeaway:** because `McpClientTool : AIFunction`, MCP tools drop straight into
the M.E.AI pipeline. The `FunctionInvokingChatClient` middleware executes each
requested tool and feeds the result back — no orchestration code.

---

## Step 3a — the tool-calling loop, by hand (optional but recommended)

Step 3 said "you never write a `while` loop." True — but the single most important
idea in MCP *is* that loop, so write it once before letting `UseFunctionInvocation`
own it forever. This is the same transport and tool discovery from Step 2, with an LLM
added and **no** function-invocation middleware — you run the round-trips yourself.

```csharp
// llm  : your provider's IChatClient (OpenAI / DeepSeek / Ollama) — NO UseFunctionInvocation
// tools: await client.ListToolsAsync()  — each McpClientTool IS an AIFunction

var options  = new ChatOptions { Tools = [.. tools] };
var messages = new List<ChatMessage>
{
    new(ChatRole.User, "How do I create an Azure Container App with managed identity?")
};

while (true)
{
    var response = await llm.GetResponseAsync(messages, options);
    messages.AddRange(response.Messages);                 // keep the assistant turn

    var calls = response.Messages
        .SelectMany(m => m.Contents)
        .OfType<FunctionCallContent>()
        .ToList();

    if (calls.Count == 0)                                  // model stopped asking for tools
    {
        Console.WriteLine(response.Text);                  // the grounded answer
        break;
    }

    foreach (var call in calls)                            // run each requested tool…
    {
        var tool = tools.First(t => t.Name == call.Name);
        object? result = await tool.InvokeAsync(
            new AIFunctionArguments(call.Arguments), CancellationToken.None);

        messages.Add(new ChatMessage(ChatRole.Tool,        // …and feed the result back
            [new FunctionResultContent(call.CallId, result)]));
    }
    // loop: the model now sees the tool output and either calls again or answers
}
```

That's the entire mechanism: **ask → the model may request tools → you execute them →
you hand the results back → repeat until it answers.** Nothing more.

**Now compare with Step 3.** Everything you just wrote is what
`FunctionInvokingChatClient` does internally — including `MaximumIterationsPerRequest`,
which is just a cap on how many times this `while` runs. That's why Step 3's version is
one `GetResponseAsync` call: the loop moved into middleware, not out of existence.

**Takeaway:** the framework doesn't remove the loop — it *hides* it. Having written it
once, you now know exactly what `UseFunctionInvocation` and the iteration bound are
doing on your behalf.

---

## Step 4 — See inside the loop: tracing middleware

`You are here:  [Client] ──▶ [Server] + [LLM loop] + [tracer inside the loop]`

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

`You are here:  [Client] ──▶ [YOUR Server]    (same client, local stdio)`

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

**Expected output** of `--local --list`:

```
Discovered 3 tool(s):
  • days_until — Calculate the number of days between today and a given date…
  • http_status — Look up an HTTP status code and get its meaning…
  • reverse_text — Reverse a string and count its characters and words…
```

The three tools you authored in `Program.cs`, discovered over stdio — same client, no key.

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

### Not just tools — invoke the prompt and read the resource

Your local server exposes all three MCP primitives. Tools you've already called; here
are the other two, straight from the client:

```csharp
// PROMPT — user-driven template. The server expands it; the host decides what to do next.
var prompt = await client.GetPromptAsync("code_review",
    new Dictionary<string, object?> { ["language"] = "csharp", ["focus"] = "async" });
Console.WriteLine(prompt.Messages[0].Content);      // the expanded review instructions

// RESOURCE — app-driven data, addressed by URI. No model decision, no side effects.
var res = await client.ReadResourceAsync("dotnet-versions");
Console.WriteLine(res.Contents[0].Text);            // the markdown version table
```

**Expected output** (abbreviated): the prompt prints an expanded code-review message
parameterised on `csharp`/`async`; the resource prints the `.NET` versions markdown
table verbatim.

> **Signature check:** `GetPromptAsync` / `ReadResourceAsync` argument and return shapes
> are worth confirming against SDK 1.4.0 with the same reflection trick — the *concept*
> (prompt = user-picked template, resource = app-attached data) is what matters and
> won't change.

**Takeaway:** **MCP ≠ just tools.** A tool is model-controlled and *acts*; a prompt is
user-controlled and *templates*; a resource is app-controlled and *provides data*. Your
local server demonstrates all three — and the WPF app in Step 6 could surface the
prompt as a slash-command and the resource as attached context.

**Try it yourself:** add a second resource to `Program.cs` (e.g. `azure-regions`
returning a short list), rebuild the server, and read it with `ReadResourceAsync` — no
client change needed. Dynamic discovery covers resources too.

---

## Step 6 — The destination: the full WPF GUI

`You are here:  everything above, behind a GUI  (zero new MCP concepts)`

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

**Try it yourself — the graduation exercise:** add a `to_upper` tool to the local
server (`Program.cs`), rebuild, then **without touching the CLI or WPF code** run
`--local --list` and open the WPF app. Your new tool appears in both, and the LLM can
call it. If you understand *why* nothing downstream needed editing, you've understood
the whole tutorial: **the client is server-agnostic, and discovery is dynamic.**

---

## Where to go next

- **[README](../README.md)** — full reference: concepts, security model, provider
  config, troubleshooting.
- **[DEMO.md](../DEMO.md)** — a guided walkthrough of the WPF app.
- **Extend it** — add your own tool to the local server, then watch the CLI and
  WPF discover it automatically (dynamic discovery in action).
