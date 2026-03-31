using System.Collections.Concurrent;
using Shikigami.Core.Models;

namespace Shikigami.Core.State;

/// <summary>
/// Thread-safe in-memory state store for all shikigami, pools, prompts, and messages.
/// Equivalent to Python state.py — the single source of truth.
/// </summary>
public sealed class ShikigamiState
{
    public ConcurrentDictionary<string, AgentRecord> Agents { get; } = new();
    public ConcurrentDictionary<string, MessageQueue> Queues { get; } = new();
    public ConcurrentDictionary<string, PromptRecord> Prompts { get; } = new();
    public ConcurrentQueue<TrashEntry> Trash { get; } = new();
    public ConcurrentDictionary<string, PoolRecord> Pools { get; } = new();

    public int HttpPort { get; set; }
    public string DefaultWorkdir { get; set; } = "";

    private double _totalCost;
    private readonly object _costLock = new();

    public double TotalCost
    {
        get { lock (_costLock) return _totalCost; }
    }

    public ShikigamiState()
    {
        Queues["lead"] = new MessageQueue();
    }

    /// <summary>
    /// Atomically adjust total cost by delta.
    /// </summary>
    public void AddCost(double delta)
    {
        lock (_costLock) _totalCost += delta;
    }

    /// <summary>
    /// Atomically update an agent's cost and adjust the total by the delta.
    /// </summary>
    public void UpdateAgentCost(AgentRecord agent, double newCost)
    {
        lock (_costLock)
        {
            _totalCost += newCost - agent.CostUsd;
            agent.CostUsd = newCost;
        }
    }

    /// <summary>
    /// Atomically update a pool agent's cost and adjust the total by the delta.
    /// </summary>
    public void UpdatePoolAgentCost(PoolAgentInfo agent, double newCost)
    {
        lock (_costLock)
        {
            _totalCost += newCost - agent.CostUsd;
            agent.CostUsd = newCost;
        }
    }

    /// <summary>
    /// Move a message to the trash bin.
    /// </summary>
    public void ToTrash(MessageRecord msg, string recipientId, string reason)
    {
        Trash.Enqueue(new TrashEntry
        {
            SenderId = msg.SenderId,
            Text = msg.Text,
            Timestamp = msg.Timestamp,
            RecipientId = recipientId,
            Reason = reason,
        });
    }

    /// <summary>
    /// Move a pool message to pool-local trash.
    /// </summary>
    public static void PoolToTrash(PoolRecord pool, MessageRecord msg, string recipientId, string reason)
    {
        lock (pool.Trash)
        {
            pool.Trash.Add(new TrashEntry
            {
                SenderId = msg.SenderId,
                Text = msg.Text,
                Timestamp = msg.Timestamp,
                RecipientId = recipientId,
                Reason = reason,
            });
        }
    }

    /// <summary>
    /// Mark a prompt-mode agent as dead. Moves queued messages to trash.
    /// </summary>
    public void MarkDead(string agentId)
    {
        if (!Agents.TryGetValue(agentId, out var agent)) return;
        agent.Active = false;

        if (Queues.TryRemove(agentId, out var queue))
        {
            foreach (var msg in queue.DrainAll())
                ToTrash(msg, agentId, "agent_died");
        }
    }

    /// <summary>
    /// Mark a Horde pool agent as dead. Returns in-progress tasks to pending.
    /// </summary>
    public void MarkDeadPoolAgent(string poolId, string agentId)
    {
        if (!Pools.TryGetValue(poolId, out var pool)) return;

        if (pool.Agents.TryGetValue(agentId, out var agentInfo))
        {
            agentInfo.Active = false;
            agentInfo.State = "dead";
        }

        // Return in-progress tasks assigned to this agent back to pending
        foreach (var task in pool.Tasks.Values)
        {
            if (task.AssignedTo == agentId && task.Status == "in_progress")
            {
                task.Status = "pending";
                task.AssignedTo = null;
                task.StartedAt = null;
            }
        }

        // Clean up queue
        if (pool.Queues.TryRemove(agentId, out var queue))
        {
            foreach (var msg in queue.DrainAll())
                PoolToTrash(pool, msg, agentId, "agent_died");
        }
    }
}
