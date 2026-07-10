namespace LearnMcpTutorial.Cli;

/// <summary>
/// Parses the CLI's command line. Extracted from the top-level statements in
/// Program.cs so it can be tested without running the whole program.
/// </summary>
internal static class CliArgs
{
    /// <summary>
    /// Pulls the recognized flags out of <paramref name="args"/> in any order;
    /// whatever remains, joined by spaces, is the question.
    /// </summary>
    internal static (bool UseLocal, bool ListOnly, string Question) Parse(string[] args)
    {
        var rest = args.ToList();
        var useLocal = rest.Remove("--local");
        var listOnly = rest.Remove("--list");
        return (useLocal, listOnly, string.Join(' ', rest).Trim());
    }
}
