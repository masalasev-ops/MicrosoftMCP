namespace LearnMcpTutorial.Tests;

/// <summary>
/// Integration test: launches the real LearnMcpTutorial.Server over stdio and
/// verifies the client discovers its tools. Proves the whole client↔server
/// handshake works end-to-end (transport, initialize, tools/list).
///
/// The server is started once by <see cref="LocalServerFixture"/> and shared.
/// </summary>
[Trait("Category", "Integration")]
[Collection(LocalServerCollection.Name)]
public class LocalServerIntegrationTests(LocalServerFixture fixture)
{
    [Fact]
    public void Client_ConnectsToLocalStdioServer_AndDiscoversTools()
    {
        var client = fixture.LearnClient;

        Assert.True(client.Connected);
        Assert.Equal("stdio (local process)", client.TransportDescription);

        var toolNames = client.Tools.Select(t => t.Name).ToHashSet();
        Assert.Equal(3, toolNames.Count);
        Assert.Contains("reverse_text", toolNames);
        Assert.Contains("http_status", toolNames);
        Assert.Contains("days_until", toolNames);
    }
}
