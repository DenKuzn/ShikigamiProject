namespace Shikigami.Runner.Services;

/// <summary>
/// Result of a single CLI turn (message → response).
/// Contains the response text, markers, usage stats, and raw events.
/// </summary>
public sealed class RunResult
{
    public string ResultText { get; set; } = "";
    public string LastTextBlock { get; set; } = "";
    public string? MarkedResult { get; set; }
    public int ToolsUsed { get; set; }
    public double? Cost { get; set; }
    public List<Dictionary<string, object>> Events { get; set; } = new();
    public string? Error { get; set; }
    public int ContextWindow { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}
