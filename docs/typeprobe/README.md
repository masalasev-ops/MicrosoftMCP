# Appendix: Verifying the SDK Surface by Reflection

**Lesson: don't trust training data about fast-moving APIs — verify against the
actual assembly.**

While building this tutorial, several assumptions about the MCP and
`Microsoft.Extensions.AI` SDKs turned out to be wrong or outdated (constructor
shapes, property names, whether a type even existed). Rather than guess, we
loaded the shipped DLLs and reflected over them to read the *real* API surface.

[Probe.cs](Probe.cs) is a minimal example: it loads `Microsoft.Extensions.AI.dll`
and prints the properties and constructors of `FunctionInvokingChatClient`. The
same technique confirmed, for this repo:

- `McpClient.RegisterNotificationHandler(string, Func<JsonElement?, CancellationToken, Task>)`
  — the real listChanged-handler signature (Phase 0).
- `StdioClientTransport(StdioClientTransportOptions { Name, Command, Arguments })`
  — the client-side stdio transport (Phase 2).
- `McpServerToolAttribute` / `McpServerPromptAttribute` / `McpServerResourceAttribute`
  and the `McpServerTool.Create(...)` + `StdioServerTransport(options, loggerFactory)`
  + `McpServer.Create(...)` construction path (Phase 1).

These findings are recorded in the README's **Verified Facts** table, each with
its source. This folder is documentation, not part of the build.
