using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// ── Bootstrap ──────────────────────────────────────────────────────────
// IMPORTANT: stdio MCP servers use stdout exclusively for the JSON-RPC
// protocol. All logging MUST go to stderr, or it will corrupt the protocol
// stream and break the client. LogToStandardErrorThreshold=Trace routes
// every log level to stderr.
using var loggerFactory = LoggerFactory.Create(b => b
    .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
    .SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("MCP-Server");

logger.LogInformation("Starting LearnMcpTutorial MCP Server over stdio...");

// ── Server Options ─────────────────────────────────────────────────────
var options = new McpServerOptions
{
    ServerInfo = new()
    {
        Name = "LearnMcpTutorial.Server",
        Version = "1.0.0"
    },
    ServerInstructions = "A tutorial MCP server with text utilities, HTTP status lookup, and .NET version history.",
    // The primitive collections are null by default — initialize them so we
    // can register tools, prompts, and resources below.
    ToolCollection = [],
    PromptCollection = [],
    ResourceCollection = []
};

// ── Tool 1: reverse_text ───────────────────────────────────────────────
// SECURITY / TRUST: every tool here declares MCP "annotations" that tell the
// client how risky it is:
//   ReadOnly   = true  → performs no writes / side effects (safe to call freely)
//   Idempotent = true  → same input always yields the same result
//   OpenWorld  = false → does not touch external systems (no network/filesystem)
//   Destructive= false → cannot delete or overwrite anything
// A well-behaved host can auto-approve read-only tools but should require
// human-in-the-loop confirmation before invoking a Destructive/write tool.
// All three tools in this tutorial are pure and read-only by design.
var reverseTextTool = McpServerTool.Create(
    (string text) =>
    {
        var reversed = new string(text.Reverse().ToArray());
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return $"Reversed: \"{reversed}\"\nCharacters: {text.Length}\nWords: {words.Length}";
    },
    new McpServerToolCreateOptions
    {
        Name = "reverse_text",
        Description = "Reverse a string and count its characters and words. A simple utility tool for text analysis.",
        Title = "Reverse Text",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false
    });

options.ToolCollection.Add(reverseTextTool);

// ── Tool 2: http_status ────────────────────────────────────────────────
var httpStatusCodes = new Dictionary<int, string>
{
    [200] = "OK — The request succeeded.",
    [201] = "Created — A new resource was created.",
    [204] = "No Content — The request succeeded but returns no body.",
    [301] = "Moved Permanently — The resource has a new permanent URL.",
    [400] = "Bad Request — The server cannot process the request.",
    [401] = "Unauthorized — Authentication is required.",
    [403] = "Forbidden — The client does not have access rights.",
    [404] = "Not Found — The requested resource was not found.",
    [405] = "Method Not Allowed — The HTTP method is not supported.",
    [429] = "Too Many Requests — Rate limit exceeded.",
    [500] = "Internal Server Error — A generic server error.",
    [502] = "Bad Gateway — Invalid response from upstream server.",
    [503] = "Service Unavailable — The server is temporarily unavailable."
};

var httpStatusTool = McpServerTool.Create(
    (int code) =>
    {
        if (httpStatusCodes.TryGetValue(code, out var description))
            return $"{code}: {description}";
        return $"Unknown status code: {code}. Known codes: {string.Join(", ", httpStatusCodes.Keys.OrderBy(k => k))}";
    },
    new McpServerToolCreateOptions
    {
        Name = "http_status",
        Description = "Look up an HTTP status code and get its meaning. Returns a description for known codes or suggests known codes for unknown ones.",
        Title = "HTTP Status Lookup",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false
    });

options.ToolCollection.Add(httpStatusTool);

// ── Tool 3: days_until ─────────────────────────────────────────────────
var daysUntilTool = McpServerTool.Create(
    (string dateString) =>
    {
        if (!DateTime.TryParse(dateString, out var target))
            return $"Could not parse \"{dateString}\". Try: YYYY-MM-DD, \"next Friday\", or \"December 25\".";

        var today = DateTime.Today;
        if (target.Date < today)
            return $"\"{target:yyyy-MM-dd}\" was {(today - target.Date).Days} days ago (past).";

        var days = (target.Date - today).Days;
        return $"{(days == 0 ? "Today" : $"{days} day{(days == 1 ? "" : "s")}")} until {target:yyyy-MM-dd} ({(days == 0 ? "now" : $"{target.DayOfWeek}")}).";
    },
    new McpServerToolCreateOptions
    {
        Name = "days_until",
        Description = "Calculate the number of days between today and a given date. Accepts formats like YYYY-MM-DD, named dates (\"next Friday\"), or month names (\"December 25\").",
        Title = "Days Until",
        ReadOnly = true,
        // Not idempotent: the result depends on today's date, so the same input
        // returns a different answer tomorrow. Still read-only — no side effects.
        Idempotent = false,
        OpenWorld = false,
        Destructive = false
    });

options.ToolCollection.Add(daysUntilTool);

// ── Prompt: code_review ────────────────────────────────────────────────
var codeReviewPrompt = McpServerPrompt.Create(
    (string? language, string? focus) =>
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "the code" : language;
        var areas = string.IsNullOrWhiteSpace(focus) ? "correctness, readability, performance, and security" : focus;
        return $"Review the following {lang} code with a focus on {areas}. " +
               "For each issue found:\n" +
               "1. Explain the problem clearly\n" +
               "2. Show the problematic code snippet\n" +
               "3. Provide a corrected version\n" +
               "4. Rate severity: 🔴 critical / 🟡 medium / 🟢 low\n\n" +
               "End with a summary of total issues found.";
    },
    new McpServerPromptCreateOptions
    {
        Name = "code_review",
        Description = "Generate a structured code review prompt. Optionally specify language and focus areas (e.g., correctness, performance).",
        Title = "Code Review"
    });

options.PromptCollection.Add(codeReviewPrompt);

// ── Resource: dotnet-versions ──────────────────────────────────────────
var dotnetVersionsResource = McpServerResource.Create(
    () => """
        .NET Version History
        ====================

        | Version  | Release Date | Support ends  | LTS? |
        |----------|-------------|---------------|------|
        | .NET 10  | Nov 2025    | Nov 2028      | Yes  |
        | .NET 9   | Nov 2024    | May 2026      | No   |
        | .NET 8   | Nov 2023    | Nov 2026      | Yes  |
        | .NET 7   | Nov 2022    | May 2024      | No   |
        | .NET 6   | Nov 2021    | Nov 2024      | Yes  |
        | .NET 5   | Nov 2020    | May 2022      | No   |

        .NET releases every November. Even-numbered versions are Long-Term
        Support (LTS) with 3 years of support; odd-numbered versions are
        Standard-Term Support (STS) with 18 months of support.
        """,
    new McpServerResourceCreateOptions
    {
        UriTemplate = "dotnet-versions",
        Name = "dotnet_versions",
        Description = "A reference table of .NET version history with release dates, support end dates, and LTS status.",
        Title = ".NET Version History",
        MimeType = "text/markdown"
    });

options.ResourceCollection.Add(dotnetVersionsResource);

// ── Run ────────────────────────────────────────────────────────────────
var transport = new StdioServerTransport(options, loggerFactory);
var server = McpServer.Create(transport, options, loggerFactory, serviceProvider: null);

logger.LogInformation(
    "Server ready: {ToolCount} tools, {PromptCount} prompts, {ResourceCount} resources",
    options.ToolCollection.ToArray().Length,
    options.PromptCollection.ToArray().Length,
    options.ResourceCollection.ToArray().Length);

await server.RunAsync();
