using LearnMcpTutorial.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace LearnMcpTutorial.Tests;

/// <summary>
/// Integration test: launches the real LearnMcpTutorial.Server over stdio and
/// verifies the client discovers its tools. Proves the whole client↔server
/// handshake works end-to-end (transport, initialize, tools/list).
///
/// Requires the server project to be built — the test project references it as
/// a build-only dependency, so a normal build satisfies this.
/// </summary>
[Trait("Category", "Integration")]
public class LocalServerIntegrationTests
{
    [Fact]
    public async Task Client_ConnectsToLocalStdioServer_AndDiscoversTools()
    {
        var transport = new StdioTransportConfig(
            Command: "dotnet",
            Arguments: ["run", "--project", ResolveServerProjectPath(), "--no-build"],
            Label: "LearnMcpTutorial.Server");

        await using var client = new LearnMcpClient(NullLogger<LearnMcpClient>.Instance, transport);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await client.ConnectAsync(cts.Token);

        Assert.True(client.Connected);
        Assert.Equal("stdio (local process)", client.TransportDescription);

        var toolNames = client.Tools.Select(t => t.Name).ToHashSet();
        Assert.Equal(3, toolNames.Count);
        Assert.Contains("reverse_text", toolNames);
        Assert.Contains("http_status", toolNames);
        Assert.Contains("days_until", toolNames);
    }

    // Walk up from the test's base directory to the repo root (MicrosoftMCP.slnx),
    // then locate the server project.
    private static string ResolveServerProjectPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MicrosoftMCP.slnx")))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException("Could not locate repo root (MicrosoftMCP.slnx).");

        return Path.GetFullPath(Path.Combine(dir.FullName, "src", "LearnMcpTutorial.Server"));
    }
}
