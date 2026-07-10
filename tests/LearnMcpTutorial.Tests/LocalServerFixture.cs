using LearnMcpTutorial.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace LearnMcpTutorial.Tests;

/// <summary>
/// Launches the real LearnMcpTutorial.Server over stdio once for the whole test
/// run, rather than once per test class.
///
/// Two clients are exposed because they answer different questions:
///   • <see cref="LearnClient"/> is the type under test in the integration test,
///     and its Tools collection carries everything needed to invoke tools and
///     inspect their annotations.
///   • <see cref="RawClient"/> reaches prompts and resources, which
///     <see cref="LearnMcpClient"/> does not surface. Adding an accessor purely
///     for tests would widen the production API, so the tests open their own
///     session instead.
///
/// Requires the server project to be built. The test project references it as a
/// build-only dependency, so a normal build satisfies that.
/// </summary>
public sealed class LocalServerFixture : IAsyncLifetime
{
    /// <summary>
    /// Generous on purpose: `dotnet run --no-build` has to start a child process,
    /// and a cold CI runner is far slower than a warm dev box. This is a guard
    /// against hanging forever, not a performance assertion.
    /// </summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(2);

    public LearnMcpClient LearnClient { get; private set; } = null!;
    public McpClient RawClient { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Same resolution logic the CLI and WPF app use, so the tests exercise it
        // instead of duplicating a repo-root walk.
        var config = LocalServerLocator.Resolve(configuredPath: null);

        LearnClient = new LearnMcpClient(NullLogger<LearnMcpClient>.Instance, config);
        await LearnClient.ConnectAsync(connectTimeout: StartTimeout);

        using var cts = new CancellationTokenSource(StartTimeout);
        RawClient = await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = config.Label,
                Command = config.Command,
                Arguments = [.. config.Arguments]
            }),
            cancellationToken: cts.Token);
    }

    public async Task DisposeAsync()
    {
        if (RawClient is not null) await RawClient.DisposeAsync();
        if (LearnClient is not null) await LearnClient.DisposeAsync();
    }
}

/// <summary>
/// Binds <see cref="LocalServerFixture"/> to every test class that declares
/// <c>[Collection(LocalServerCollection.Name)]</c>. xUnit also serializes those
/// classes against each other, which keeps the two stdio sessions predictable.
/// </summary>
[CollectionDefinition(Name)]
public sealed class LocalServerCollection : ICollectionFixture<LocalServerFixture>
{
    public const string Name = "LocalServer";
}
