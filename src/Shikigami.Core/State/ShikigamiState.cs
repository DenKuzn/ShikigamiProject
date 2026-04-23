using System.Collections.Concurrent;
using System.Text.Json;
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
    /// Auto-notifies the agent's parent via child_update event.
    /// </summary>
    public void MarkDead(string agentId)
    {
        if (!Agents.TryGetValue(agentId, out var agent)) return;
        var wasActive = agent.Active;
        agent.Active = false;

        if (Queues.TryRemove(agentId, out var queue))
        {
            foreach (var msg in queue.DrainAll())
                ToTrash(msg, agentId, "agent_died");
        }

        if (wasActive)
        {
            if (agent.GetState() is not ("completed" or "failed" or "idle" or "taken"))
                agent.CurrentStep = "dead";
            NotifyParent(agent);
        }
    }

    /// <summary>
    /// Enqueue a child_update event to the agent's parent queue.
    /// Event format: Text = "[child_update] {child_id, name, current_step}".
    /// Silent no-op if agent has no ParentId (shouldn't happen for registered agents).
    /// </summary>
    public void NotifyParent(AgentRecord agent)
    {
        var parentId = string.IsNullOrEmpty(agent.ParentId) ? "lead" : agent.ParentId;
        var payload = new Dictionary<string, object?>
        {
            ["child_id"] = agent.Id,
            ["name"] = agent.Name,
            ["current_step"] = agent.CurrentStep,
        };

        var json = JsonSerializer.Serialize(payload);
        var msg = new MessageRecord
        {
            SenderId = agent.Id,
            Text = "[child_update] " + json,
        };
        var queue = Queues.GetOrAdd(parentId, _ => new MessageQueue());
        queue.Enqueue(msg);
    }

    /// <summary>
    /// Enqueue a task_update event into both the pool-local "lead" queue AND
    /// the global queue of the pool's creator. Dual-push so leads can use a
    /// single long-poll on their global inbox to see all pool events.
    /// </summary>
    public void NotifyPoolTask(PoolRecord pool, TaskRecord task, string? agentId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["pool_id"] = pool.Id,
            ["task_id"] = task.Id,
            ["title"] = task.Title,
            ["status"] = task.Status,
            ["agent_id"] = agentId,
        };

        var json = JsonSerializer.Serialize(payload);
        var text = "[task_update] " + json;
        var sender = string.IsNullOrEmpty(agentId) ? pool.Id : agentId!;

        // Pool-local lead queue (legacy channel)
        var poolLeadQ = pool.Queues.GetOrAdd("lead", _ => new MessageQueue());
        poolLeadQ.Enqueue(new MessageRecord { SenderId = sender, Text = text });

        // Global queue of the pool's creator
        var globalQ = Queues.GetOrAdd(pool.LeadId, _ => new MessageQueue());
        globalQ.Enqueue(new MessageRecord { SenderId = sender, Text = text });
    }

    /// <summary>
    /// Enqueue a pool_update event when the pool itself transitions to a terminal state.
    /// </summary>
    public void NotifyPoolTerminal(PoolRecord pool)
    {
        var tasks = pool.Tasks.Values;
        var payload = new Dictionary<string, object?>
        {
            ["pool_id"] = pool.Id,
            ["name"] = pool.Name,
            ["status"] = pool.Status,
            ["tasks_total"] = tasks.Count(),
            ["tasks_completed"] = tasks.Count(t => t.Status == "completed"),
            ["tasks_failed"] = tasks.Count(t => t.Status == "failed"),
        };
        var json = JsonSerializer.Serialize(payload);
        var text = "[pool_update] " + json;

        var poolLeadQ = pool.Queues.GetOrAdd("lead", _ => new MessageQueue());
        poolLeadQ.Enqueue(new MessageRecord { SenderId = pool.Id, Text = text });

        var globalQ = Queues.GetOrAdd(pool.LeadId, _ => new MessageQueue());
        globalQ.Enqueue(new MessageRecord { SenderId = pool.Id, Text = text });
    }

    /// <summary>
    /// Mark a Horde pool agent as dead. Returns in-progress tasks to pending.
    /// Notifies pool lead about each reopened task.
    /// </summary>
    public void MarkDeadPoolAgent(string poolId, string agentId)
    {
        if (!Pools.TryGetValue(poolId, out var pool)) return;

        if (pool.Agents.TryGetValue(agentId, out var agentInfo))
        {
            agentInfo.Active = false;
            agentInfo.State = "dead";
        }

        var reopened = new List<TaskRecord>();
        foreach (var task in pool.Tasks.Values)
        {
            if (task.AssignedTo == agentId && task.Status == "in_progress")
            {
                task.Status = "pending";
                task.AssignedTo = null;
                task.StartedAt = null;
                reopened.Add(task);
            }
        }

        if (pool.Queues.TryRemove(agentId, out var queue))
        {
            foreach (var msg in queue.DrainAll())
                PoolToTrash(pool, msg, agentId, "agent_died");
        }

        foreach (var t in reopened)
            NotifyPoolTask(pool, t, agentId);
    }
}
