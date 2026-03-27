namespace Shikigami.Core.Models;

/// <summary>
/// Represents a registered shikigami (prompt-mode agent).
/// </summary>
public sealed class AgentRecord
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Task { get; set; }
    public required string ParentId { get; set; }
    public required int Pid { get; set; }
    public required string AgentType { get; set; }
    public bool Active { get; set; } = true;
    public string RegisteredAt { get; set; } = DateTime.UtcNow.ToString("o");
    public string? Status { get; set; }
    public string? CurrentStep { get; set; }
    public object? Metadata { get; set; }
    public string? Result { get; set; }
    public object? EventLog { get; set; }
    public double CostUsd { get; set; }
}
