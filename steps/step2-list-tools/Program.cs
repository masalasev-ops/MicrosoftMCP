// ─────────────────────────────────────────────────────────────────────────
// TUTORIAL STEP 2 — Your first MCP client (~40 lines, from scratch)
//
// Goal: connect to the Microsoft Learn MCP Server and print its tools.
// No LLM, no agent — just the MCP handshake and dynamic tool discovery.
//
// This project depends ONLY on the ModelContextProtocol package, so it stands
// completely on its own. Run it with:
//     dotnet run --project steps/step2-list-tools
//
// Next step (docs/TUTORIAL.md, Step 3): feed these tools to an LLM.
// ─────────────────────────────────────────────────────────────────────────

using ModelContextProtocol.Client;

// 1. Describe the transport. The Learn server speaks Streamable HTTP.
var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = new Uri("https://learn.microsoft.com/api/mcp"),
    TransportMode = HttpTransportMode.StreamableHttp
});

// 2. Create the client. No authentication — the Learn server is public.
Console.WriteLine("Connecting to the Microsoft Learn MCP Server...");
await using var client = await McpClient.CreateAsync(transport);

// 3. Discover tools AT RUNTIME. Never hardcode tool names — the server is the
//    source of truth, and its tools can change over time.
var tools = await client.ListToolsAsync();

// 4. Print what we found. Each McpClientTool is also a Microsoft.Extensions.AI
//    AIFunction, which is what lets an LLM call it in later steps.
Console.WriteLine($"\nDiscovered {tools.Count} tool(s):\n");
foreach (var tool in tools)
{
    Console.WriteLine($"  • {tool.Name}");
    Console.WriteLine($"      {tool.Description}");
    Console.WriteLine();
}

Console.WriteLine("That's the whole client. You just spoke MCP.");
