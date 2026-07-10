using LearnMcpTutorial.Agent;
using LearnMcpTutorial.Diagnostics;
using LearnMcpTutorial.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace LearnMcpTutorial.Tests;

/// <summary>
/// Unit tests for <see cref="DocsAgent"/> using a scripted <see cref="IChatClient"/>.
/// No network, no MCP server, no tokens — the agent's pipeline, multi-turn
/// history, and tool-calling loop are all exercised deterministically.
/// </summary>
public class DocsAgentTests
{
    // A never-connected client is fine: the happy paths below never touch it
    // (it's only used on the stale-schema retry path, which we don't trigger).
    private static LearnMcpClient DummyClient() =>
        new(NullLogger<LearnMcpClient>.Instance, new HttpTransportConfig("https://example.invalid"));

    private static DocsAgent MakeAgent(IChatClient llm, Action<TraceStep>? onStep = null) =>
        new(llm, DummyClient(), NullLogger<DocsAgent>.Instance, maxToolIterations: 10, onToolStep: onStep);

    [Fact]
    public async Task AskAsync_ReturnsAnswer_AndExtractsCitedUrls()
    {
        var answer = """
            To create a Container App, run `az containerapp create`.

            ## Sources
            https://learn.microsoft.com/en-us/cli/azure/containerapp
            """;
        var agent = MakeAgent(new ScriptedChatClient(ScriptedChatClient.Text(answer)));

        var result = await agent.AskAsync("How do I create a Container App?", tools: []);

        Assert.Contains("az containerapp create", result.Answer);
        Assert.Single(result.CitedUrls);
        Assert.Contains("https://learn.microsoft.com/en-us/cli/azure/containerapp", result.CitedUrls);
    }

    [Fact]
    public async Task AskAsync_InvokesTool_AndFiresTraceCallback()
    {
        // The fake tool records that it ran and returns a canned result.
        var toolInvoked = false;
        AIFunction fakeTool = AIFunctionFactory.Create(
            (string query) => { toolInvoked = true; return "HTTP 404 means Not Found."; },
            name: "fake_search",
            description: "A fake documentation search tool.");

        // Round 1: LLM asks for the tool. Round 2: LLM answers using the result.
        var llm = new ScriptedChatClient(
            ScriptedChatClient.ToolCall("call-1", "fake_search", new() { ["query"] = "http 404" }),
            ScriptedChatClient.Text("HTTP 404 means Not Found. https://learn.microsoft.com/http/404"));

        var steps = new List<TraceStep>();
        var agent = MakeAgent(llm, onStep: s => steps.Add(s));

        var result = await agent.AskAsync("What is HTTP 404?", tools: [fakeTool]);

        Assert.True(toolInvoked, "the fake tool should have been executed by the loop");
        Assert.Contains("Not Found", result.Answer);
        // The tracing wrapper should have reported the tool call and the text.
        Assert.Contains(steps, s => s.Type == TraceStepType.ToolCallsRequested
                                    && s.ToolCalls.Any(tc => tc.FunctionName == "fake_search"));
        Assert.Contains(steps, s => s.Type == TraceStepType.TextProduced);
    }

    [Fact]
    public async Task AskAsync_IsMultiTurn_SecondCallSeesFirstQuestion()
    {
        var llm = new ScriptedChatClient(
            ScriptedChatClient.Text("First answer."),
            ScriptedChatClient.Text("Second answer, with context."));
        var agent = MakeAgent(llm);

        await agent.AskAsync("What is an Azure Container App?", tools: []);
        await agent.AskAsync("How do I scale it?", tools: []);

        Assert.Equal(2, agent.TurnCount);

        // The second call's message list must include the FIRST question and its
        // answer — proving history carried over (multi-turn context).
        var secondCallMessages = llm.ReceivedMessageSets[1];
        var joined = string.Join("\n", secondCallMessages.Select(m => m.Text));
        Assert.Contains("Azure Container App", joined);      // first user question
        Assert.Contains("First answer", joined);              // first assistant answer
        Assert.Contains("How do I scale it?", joined);        // second user question
    }

    [Fact]
    public async Task ResetConversation_ClearsHistory()
    {
        var llm = new ScriptedChatClient(
            ScriptedChatClient.Text("A1"),
            ScriptedChatClient.Text("A2"));
        var agent = MakeAgent(llm);

        await agent.AskAsync("Q1", tools: []);
        Assert.Equal(1, agent.TurnCount);

        agent.ResetConversation();
        Assert.Equal(0, agent.TurnCount);

        await agent.AskAsync("Q2", tools: []);
        // After reset, the second call should NOT contain Q1.
        var secondCall = llm.ReceivedMessageSets[1];
        Assert.DoesNotContain("Q1", string.Join("\n", secondCall.Select(m => m.Text)));
    }

    [Fact]
    public async Task AskStreamingAsync_YieldsDeltas_AndPersistsTurn()
    {
        var llm = new ScriptedChatClient(
            ScriptedChatClient.Text("Streamed answer about https://learn.microsoft.com/dotnet"));
        var agent = MakeAgent(llm);

        var buffer = "";
        await foreach (var delta in agent.AskStreamingAsync("Tell me about .NET", tools: []))
            buffer += delta;

        Assert.Contains("Streamed answer", buffer);
        Assert.Equal(1, agent.TurnCount);
        var urls = DocsAgent.ExtractCitedUrls(buffer);
        Assert.Contains("https://learn.microsoft.com/dotnet", urls);
    }

    [Theory]
    [InlineData("See https://learn.microsoft.com/a and https://learn.microsoft.com/b.", 2)]
    [InlineData("Trailing punctuation https://learn.microsoft.com/x, is stripped.", 1)]
    [InlineData("No links here at all.", 0)]
    public void ExtractCitedUrls_FindsLearnUrls(string text, int expectedCount)
    {
        var urls = DocsAgent.ExtractCitedUrls(text);
        Assert.Equal(expectedCount, urls.Count);
        Assert.All(urls, u => Assert.False(
            u.EndsWith(',') || u.EndsWith('.') || u.EndsWith(';') || u.EndsWith(':'),
            $"URL should not end with trailing punctuation: {u}"));
    }
}
