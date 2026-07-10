using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace LearnMcpTutorial.Tests;

/// <summary>
/// Exercises what the local MCP server actually does: each tool's output, the
/// trust annotations it advertises, its prompt, and its resource. Network-free
/// and token-free — the server runs as a local child process.
/// </summary>
[Trait("Category", "Integration")]
[Collection(LocalServerCollection.Name)]
public class LocalServerToolTests(LocalServerFixture fixture)
{
    private McpClientTool Tool(string name) =>
        fixture.LearnClient.Tools.Single(t => t.Name == name);

    private static string TextOf(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private async Task<string> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await Tool(tool).CallAsync(args);
        // IsError is optional in the MCP schema, so it is null on success -- and
        // Assert.False(null) fails. Compare against true explicitly.
        // The message surfaces the server's own text; "Assert.False() Failure" alone tells you nothing.
        Assert.True(result.IsError is not true, $"{tool} returned an error: {TextOf(result)}");
        return TextOf(result);
    }

    // ── reverse_text ───────────────────────────────────────────────────────

    [Fact]
    public async Task ReverseText_ReversesAndCounts()
    {
        var text = await CallAsync("reverse_text", new() { ["text"] = "hello world" });

        Assert.Contains("Reversed: \"dlrow olleh\"", text);
        Assert.Contains("Characters: 11", text);
        Assert.Contains("Words: 2", text);
    }

    [Fact]
    public async Task ReverseText_CountsWordsIgnoringRepeatedSpaces()
    {
        var text = await CallAsync("reverse_text", new() { ["text"] = "a  b" });

        Assert.Contains("Words: 2", text);
        Assert.Contains("Characters: 4", text);
    }

    // ── http_status ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(200, "200: OK")]
    [InlineData(404, "404: Not Found")]
    [InlineData(503, "503: Service Unavailable")]
    public async Task HttpStatus_DescribesKnownCode(int code, string expectedPrefix)
    {
        var text = await CallAsync("http_status", new() { ["code"] = code });

        Assert.StartsWith(expectedPrefix, text);
    }

    [Fact]
    public async Task HttpStatus_ListsKnownCodes_ForUnknownCode()
    {
        var text = await CallAsync("http_status", new() { ["code"] = 799 });

        Assert.Contains("Unknown status code: 799", text);
        // The suggestion list is ordered ascending.
        Assert.Contains("200, 201, 204", text);
    }

    // ── days_until ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DaysUntil_ReportsPastDates()
    {
        var text = await CallAsync("days_until", new() { ["dateString"] = "2000-01-01" });

        Assert.Contains("2000-01-01", text);
        Assert.Contains("(past)", text);
        Assert.Contains("days ago", text);
    }

    [Fact]
    public async Task DaysUntil_ReportsToday()
    {
        var today = DateTime.Today;
        var text = await CallAsync("days_until", new() { ["dateString"] = today.ToString("yyyy-MM-dd") });

        Assert.Contains("Today until", text);
        Assert.Contains("(now)", text);
    }

    [Fact]
    public async Task DaysUntil_ReportsFutureDateWithSingularDay()
    {
        var tomorrow = DateTime.Today.AddDays(1);
        var text = await CallAsync("days_until", new() { ["dateString"] = tomorrow.ToString("yyyy-MM-dd") });

        // Singular "1 day", not "1 days", and the weekday is named.
        Assert.Contains("1 day until", text);
        Assert.DoesNotContain("1 days until", text);
        Assert.Contains(tomorrow.DayOfWeek.ToString(), text);
    }

    [Fact]
    public async Task DaysUntil_ExplainsUnparseableInput()
    {
        var result = await Tool("days_until").CallAsync(
            new Dictionary<string, object?> { ["dateString"] = "not a date" });

        Assert.Contains("Could not parse", TextOf(result));
    }

    // ── Trust annotations ──────────────────────────────────────────────────
    //
    // A host may auto-approve read-only tools, so these hints are a security
    // contract, not decoration.

    [Theory]
    [InlineData("reverse_text")]
    [InlineData("http_status")]
    [InlineData("days_until")]
    public void EveryTool_IsAnnotatedReadOnlyAndClosedWorld(string toolName)
    {
        var annotations = Tool(toolName).ProtocolTool.Annotations;

        Assert.NotNull(annotations);
        Assert.True(annotations.ReadOnlyHint);
        Assert.False(annotations.DestructiveHint);
        Assert.False(annotations.OpenWorldHint);
    }

    [Theory]
    [InlineData("reverse_text", true)]
    [InlineData("http_status", true)]
    // days_until depends on today's date, so the same input answers differently tomorrow.
    [InlineData("days_until", false)]
    public void Tools_DeclareIdempotencyAccurately(string toolName, bool expected)
    {
        Assert.Equal(expected, Tool(toolName).ProtocolTool.Annotations!.IdempotentHint);
    }

    // ── Prompt: code_review ────────────────────────────────────────────────

    private static string TextOf(GetPromptResult result) =>
        string.Concat(result.Messages.Select(m => m.Content).OfType<TextContentBlock>().Select(c => c.Text));

    [Fact]
    public async Task CodeReviewPrompt_FallsBackToDefaults_WhenNoArguments()
    {
        var result = await fixture.RawClient.GetPromptAsync("code_review");

        var text = TextOf(result);
        Assert.Contains("Review the following the code", text);
        Assert.Contains("correctness, readability, performance, and security", text);
        Assert.Contains("Rate severity", text);
    }

    [Fact]
    public async Task CodeReviewPrompt_AdvertisesBothArgumentsAsOptional()
    {
        var prompts = await fixture.RawClient.ListPromptsAsync();

        var prompt = Assert.Single(prompts);
        Assert.Equal("code_review", prompt.Name);

        var arguments = prompt.ProtocolPrompt.Arguments;
        Assert.NotNull(arguments);
        Assert.Equal(2, arguments.Count);
        // Required is derived from whether the parameter has a default value.
        // A nullable type alone is not enough.
        Assert.All(arguments, a => Assert.NotEqual(true, a.Required));
    }

    [Fact]
    public async Task CodeReviewPrompt_HonorsLanguageAndFocus()
    {
        var result = await fixture.RawClient.GetPromptAsync(
            "code_review",
            new Dictionary<string, object?> { ["language"] = "C#", ["focus"] = "thread safety" });

        var text = TextOf(result);
        Assert.Contains("Review the following C# code with a focus on thread safety", text);
        Assert.DoesNotContain("correctness, readability, performance, and security", text);
    }

    // ── Resource: dotnet-versions ──────────────────────────────────────────

    [Fact]
    public async Task DotnetVersionsResource_IsReadableMarkdown()
    {
        var result = await fixture.RawClient.ReadResourceAsync("dotnet-versions");

        var text = string.Concat(result.Contents.OfType<TextResourceContents>().Select(c => c.Text));

        Assert.Contains(".NET Version History", text);
        Assert.Contains("| .NET 10", text);
        Assert.Contains("LTS", text);
    }

    [Fact]
    public async Task DotnetVersionsResource_IsAdvertisedWithMarkdownMimeType()
    {
        var resources = await fixture.RawClient.ListResourcesAsync();

        var resource = Assert.Single(resources);
        Assert.Equal("dotnet_versions", resource.Name);
        Assert.Equal("text/markdown", resource.MimeType);
    }
}
