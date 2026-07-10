namespace LearnMcpTutorial.Mcp;

/// <summary>
/// Finds the local MCP server and describes how to launch it over stdio.
///
/// Both the CLI and the WPF app need this, so it lives in Core. It takes a
/// plain <see cref="string"/> rather than an <c>IConfiguration</c> on purpose:
/// Core stays free of a configuration dependency, and the callers pass
/// <c>LocalServer:ProjectPath</c> straight through.
/// </summary>
public static class LocalServerLocator
{
    /// <summary>Used when no path is configured.</summary>
    public const string DefaultProjectPath = "src/LearnMcpTutorial.Server";

    private const string RepoRootMarker = "MicrosoftMCP.slnx";
    private const string ServerAssemblyName = "LearnMcpTutorial.Server";

    /// <summary>
    /// A best-effort absolute project path, suitable for pre-filling an editable
    /// UI field. Never throws: if the repo root cannot be found, the configured
    /// (or default) path is returned unchanged so the user can correct it.
    /// </summary>
    public static string ResolveProjectPathForDisplay(string? configuredPath)
    {
        var relative = Normalize(configuredPath) ?? DefaultProjectPath;
        if (Path.IsPathRooted(relative)) return Path.GetFullPath(relative);

        var repoRoot = FindRepoRoot();
        return repoRoot is not null
            ? Path.GetFullPath(Path.Combine(repoRoot, relative))
            : relative;
    }

    /// <summary>
    /// Resolves how to launch the local server, in order:
    /// <list type="number">
    ///   <item>the configured project directory (absolute honored, relative
    ///         resolved against the repo root) — <c>dotnet run --project ... --no-build</c>;</item>
    ///   <item>a published <c>LearnMcpTutorial.Server.dll</c> next to this binary —
    ///         launched through its apphost when one exists, otherwise
    ///         <c>dotnet &lt;dll&gt;</c>.</item>
    /// </list>
    /// Step 2 only fires in a published layout. In a normal build the server's
    /// managed assembly is <em>not</em> copied next to the CLI or the WPF app,
    /// because both reference it with <c>ReferenceOutputAssembly="false"</c>.
    /// </summary>
    /// <exception cref="McpConnectionException">
    /// No server could be located. The message lists every path that was tried.
    /// </exception>
    public static StdioTransportConfig Resolve(string? configuredPath)
    {
        var attempted = new List<string>();

        // 1. The configured (or default) project directory.
        var projectDir = ResolveProjectPathForDisplay(configuredPath);
        attempted.Add(projectDir);
        if (Directory.Exists(projectDir) && Directory.EnumerateFiles(projectDir, "*.csproj").Any())
        {
            return new StdioTransportConfig(
                Command: "dotnet",
                Arguments: ["run", "--project", projectDir, "--no-build"],
                Label: ServerAssemblyName);
        }

        // 2. A published server next to us.
        //
        // Probe on the managed .dll, never on the apphost alone. A build with
        // ReferenceOutputAssembly="false" copies the server's .exe, .deps.json
        // and .runtimeconfig.json into our output but deliberately omits the
        // .dll -- so a lone apphost here is a stub that exits immediately with
        // "The application to execute does not exist".
        var dll = Path.Combine(AppContext.BaseDirectory, ServerAssemblyName + ".dll");
        attempted.Add(dll);
        if (File.Exists(dll))
        {
            var apphost = Path.Combine(
                AppContext.BaseDirectory,
                ServerAssemblyName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

            return File.Exists(apphost)
                ? new StdioTransportConfig(apphost, [], ServerAssemblyName)
                : new StdioTransportConfig("dotnet", [dll], ServerAssemblyName);
        }

        throw new McpConnectionException(
            $"""
             Could not find the local MCP server ({ServerAssemblyName}).

             Tried:
               {string.Join(Environment.NewLine + "  ", attempted)}

             Set LocalServer:ProjectPath in appsettings.Local.json to the server's
             project directory, or build the solution so that
             {DefaultProjectPath} exists relative to the repo root.

             (A {ServerAssemblyName}.exe with no matching .dll beside it is ignored:
             that is the stub apphost a normal build leaves behind, not a published
             server.)
             """);
    }

    /// <summary>
    /// Walks up from the running binary looking for the file that marks the repo
    /// root. Returns null outside a source checkout (e.g. a published app).
    /// </summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, RepoRootMarker)))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static string? Normalize(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : path.Trim();
}
