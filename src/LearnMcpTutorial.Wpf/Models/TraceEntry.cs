namespace LearnMcpTutorial.Wpf.Models;
public sealed class TraceEntry
{
    public string Icon { get; init; } = "";
    public string Message { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.Now;
}
