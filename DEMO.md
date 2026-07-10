# DEMO.md — WPF GUI Demo Walkthrough

This guide walks through demonstrating all MCP core concepts using the WPF GUI app.

> **Note:** Because the agent retrieves **current** documentation each run,
> your output may differ as Microsoft Learn docs evolve. That's the point —
> answers stay current.

---

## Getting Started

Windows only — WPF targets `net10.0-windows`.

```bash
dotnet run --project src/LearnMcpTutorial.Wpf
```

The app opens with two tabs: **Demo** and **Architecture**.

You can type your API key into the password box, or put it in
`appsettings.Local.json` at the repo root (copy `appsettings.Local.json.example`)
and leave the box empty. The box wins whenever it has content.

## Tab 1: Demo — Interactive Q&A

The window has four zones:

```
┌── Connection Bar ────────────────────────────────────────────────────┐
│ Provider: [▼ openai]  Model: [gpt-4o-mini]  API Key: [••••]         │
│ MCP Server: [https://learn.microsoft.com/api/mcp]  [🔗 Connect]     │
├── Question ──────────────────────────────────────────────────────────┤
│ Ask a question about Microsoft technologies                          │
│ ┌──────────────────────────────────────────┐  ┌────────┐            │
│ │ How do I create an Azure Container App    │  │  ASK   │            │
│ │ with system-assigned managed identity?    │  │ (blue) │            │
│ └──────────────────────────────────────────┘  │ Clear  │            │
│                                               └────────┘            │
├── Left: Tools ───────────┬── Right: Trace / Answer ─────────────────┤
│ Discovered Tools          │  Tool Call Trace                         │
│ ┌──────────────────────┐ │  🔧 microsoft_docs_search(...)           │
│ │ microsoft_docs_search│ │  💬 LLM: I found relevant docs...        │
│ │ microsoft_docs_fetch │ │  🔧 microsoft_docs_fetch(url)            │
│ │ microsoft_code_...   │ │  💬 LLM: Let me verify the details...    │
│ └──────────────────────┘ │  ─────────────────────────               │
│                           │  Answer & Sources                        │
│                           │  ┌────────────────────────────────────┐ │
│                           │  │ ## 1. Create Container App         │ │
│                           │  │ ```bash                            │ │
│                           │  │ az containerapp create \           │ │
│                           │  │   --system-assigned                │ │
│                           │  │ ```                                │ │
│                           │  │ [dark IDE-style code blocks]       │ │
│                           │  └────────────────────────────────────┘ │
│                           │  Sources:                                │
│                           │  1. https://learn.microsoft.com/...     │
└───────────────────────────┴──────────────────────────────────────────┘
│ 🟢 Connected — 3 tools discovered                    [progress bar] │
```

### Demo Walkthrough

1. **Select provider** — Choose OpenAI, DeepSeek, Ollama, or LM Studio from the dropdown. The Base URL field appears for DeepSeek/Ollama/LM Studio.
2. **Enter API key** — Type your key in the password box (skip for Ollama and LM Studio).
3. **Click Connect** — The app connects to the MCP server over Streamable HTTP. Three tools appear in the Discovered Tools list.
4. **Ask a question** — Type or paste a question, then click the blue **Ask** button (or press **Ctrl+Enter** or **Shift+Enter**).
5. **Watch the trace** — Each tool call appears in real-time: 🔧 for tool invocations, 💬 for LLM responses.
6. **Watch it stream** — The answer streams in **token-by-token** as the LLM
   produces it, then renders with proper formatting:
   - Code blocks have **dark IDE-style background** (`#1E1E1E`) with syntax highlighting (keywords blue, strings orange, CLI commands yellow)
   - Headers are bold and larger
   - Source URLs are blue, underlined, and **clickable** (opens in browser)
7. **Ask a follow-up** — Just ask another question. The agent remembers the
   conversation (**multi-turn**), so "How do I scale it?" understands "it" from
   your previous question. The status bar shows the turn number. **Clear** starts
   a fresh conversation.

---

## Tab 2: Architecture — Live Interactive Diagrams

Switch to the Architecture tab **while** connecting or asking a question to see
the diagrams animate in real-time.

### Live Status Banner

A colored banner at the top shows the current state:
- **Green** — connected and ready
- **Amber with gold border** — busy processing (connecting or asking)
- **Gray** — idle / waiting

The banner text updates continuously as the pipeline processes.

### Diagram 1: High-Level Architecture

Each of the four component boxes glows with a **gold border** when active:

| Component | Color | When it glows |
|-----------|-------|--------------|
| WPF Demo App | 🔵 Blue → bright cyan | Clicking Connect, asking a question |
| MCP Server | 🟢 Green → bright green | Connecting to MCP, tool calls in progress |
| Microsoft Learn | 🟣 Purple → bright violet | Tools being discovered, content being fetched |
| LLM Provider | 🟠 Orange → bright orange | LLM API call in progress |

### Diagram 2: AI Pipeline Stack

Four layers, each dimmed when inactive and glowing when active:

| When | Layer that glows |
|------|-----------------|
| Question submitted | 1. DocsAgent (blue) |
| LLM requests tool call | 2. FunctionInvokingChatClient (orange) |
| Tool call intercepted | 3. ToolCallTracingChatClient (purple) |
| LLM responds with text | 4. LLM Provider (dark/gray) |
| Done | All reset to dimmed |

### Diagram 3: End-to-End Sequence

8 steps highlight one at a time with **~700ms delay** between each:

| Step | Highlight | What happens |
|------|-----------|-------------|
| 1 | Blue border | **User asks:** (shows your actual question text from the Demo tab) |
| 2 | Orange border | LLM → `microsoft_docs_search(...)` — searches Microsoft Learn |
| 3 | Green border | MCP Server returns search results — LLM reviews them |
| 4 | Orange border | LLM → `microsoft_docs_fetch(url)` — fetches the best page |
| 5 | Green border | MCP Server returns full documentation page — LLM verifies |
| 6 | Orange border | LLM → `microsoft_code_sample_search(...)` — gets code examples |
| 7 | Green border | MCP Server returns verified code samples |
| 8 | Blue border (thick) | ✅ LLM synthesizes final answer with cited sources |

Dormant steps appear in light gray; the active step glows with full color and a thick border.

---

## What Happens Under the Hood

The agent's function invocation pipeline executes automatically:

```
1. DocsAgent prepares system prompt:
   "You MUST ground answers in Microsoft Learn docs. Always cite sources."

2. User question + MCP tools → LLM Provider

3. LLM: "I need to search for documentation"
   → tool_call: microsoft_docs_search("Azure Container App managed identity")

4. MCP Server returns: list of relevant docs with URLs and snippets

5. LLM reviews results: "I need the full page to verify details"
   → tool_call: microsoft_docs_fetch("https://learn.microsoft.com/...")

6. MCP Server returns: full markdown of the documentation page

7. LLM: "I need code samples to confirm the CLI syntax"
   → tool_call: microsoft_code_sample_search("az containerapp create system-assigned")

8. MCP Server returns: matching code samples with source URLs

9. LLM: "I have all the facts — synthesizing answer"
   → final answer with cited learn.microsoft.com sources
```

Total tool round-trips: **3** (bounded by MaximumIterationsPerRequest = 10).

The `ToolCallTracingChatClient` (layer 3 in the pipeline) intercepts every round-trip
and fires a callback that populates the UI Trace Panel and enqueues animation frames
for the Architecture tab.

---

## Demo Tips

- **Best flow:** Start on Architecture tab, switch to Demo to connect/ask, switch back to Architecture mid-question to show the live highlights.
- **Enter key:** Ctrl+Enter or Shift+Enter sends the question.
- **Clear:** Click Clear to reset everything (question, answer, trace, architecture state) — and start a fresh multi-turn conversation.
- **Multi-turn:** Ask follow-up questions without repeating context — the agent remembers prior turns. Switching provider or reconnecting starts fresh.
- **Provider switching:** Change providers anytime — the next Ask rebuilds the chat client (and resets the conversation) with new settings.
- **No API key?** Use a local model — Ollama (`ollama pull llama3.2`) or LM Studio (load a model and start its server). Select it in the dropdown and leave the API key blank.
- **Dark code theme:** Code blocks in answers render with `#1E1E1E` background, monospace font, and basic syntax coloring for C#, bash, JSON, and CLI commands.
