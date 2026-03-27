namespace Shikigami.Core.Models;

/// <summary>
/// Represents a Horde pool — a group of tasks with dependency ordering.
/// </summary>
public sealed class PoolRecord
{
    public required string Id { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "in_progress";
    public Dictionary<string, PoolAgentInfo> Agents { get; set; } = new();
    public Dictionary<string, TaskRecord> Tasks { get; set; } = new();
    public List<string> TaskOrder { get; set; } = new();
    public Dictionary<string, List<MessageRecord>> Queues { get; set; } = new() { ["lead"] = new() };
    public List<TrashEntry> Trash { get; set; } = new();
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public string? CompletedAt { get; set; }
}

public sealed class PoolAgentInfo
{
    public required string AgentType { get; set; }
    public required int Pid { get; set; }
    public bool Active { get; set; } = true;
    public string State { get; set; } = "starting";
    public string StateDetail { get; set; } = "Launching";
    public string LaunchedAt { get; set; } = DateTime.UtcNow.ToString("o");
    public double CostUsd { get; set; }
}
