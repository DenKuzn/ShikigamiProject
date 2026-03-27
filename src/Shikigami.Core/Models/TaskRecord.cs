namespace Shikigami.Core.Models;

/// <summary>
/// Represents a single task within a Horde pool.
/// </summary>
public sealed class TaskRecord
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string AgentType { get; set; }
    public string Status { get; set; } = "pending";
    public List<string> DependsOn { get; set; } = new();
    public string? AssignedTo { get; set; }
    public string? Result { get; set; }
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public string? StartedAt { get; set; }
    public string? CompletedAt { get; set; }
}
