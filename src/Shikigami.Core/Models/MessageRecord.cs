namespace Shikigami.Core.Models;

/// <summary>
/// A message between shikigami or between lead and shikigami.
/// </summary>
public sealed class MessageRecord
{
    public required string SenderId { get; set; }
    public required string Text { get; set; }
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");
}

/// <summary>
/// A message that has been consumed, rejected, or purged.
/// </summary>
public sealed class TrashEntry
{
    public required string SenderId { get; set; }
    public required string Text { get; set; }
    public required string Timestamp { get; set; }
    public required string RecipientId { get; set; }
    public required string Reason { get; set; }
    public string TrashedAt { get; set; } = DateTime.UtcNow.ToString("o");
}
