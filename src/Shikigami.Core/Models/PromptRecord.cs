namespace Shikigami.Core.Models;

/// <summary>
/// A stored prompt to be fetched by a shikigami on startup.
/// </summary>
public sealed class PromptRecord
{
    public required string Id { get; set; }
    public required string Text { get; set; }
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
}
