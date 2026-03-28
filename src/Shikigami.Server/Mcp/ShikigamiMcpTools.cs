using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Shikigami.Core.Models;
using Shikigami.Core.Services;
using Shikigami.Core.State;

namespace Shikigami.Server.Mcp;

/// <summary>
/// MCP tools exposed to Claude Code main chat for shikigami management.
/// </summary>
[McpServerToolType]
public sealed class ShikigamiMcpTools
{
    private readonly ShikigamiState _state;
    private readonly LaunchService _launcher;
    private readonly PoolService _poolService;

    public ShikigamiMcpTools(ShikigamiState state, LaunchService launcher, PoolService poolService)
    {
        _state = state;
        _launcher = launcher;
        _poolService = poolService;
    }

    [McpServerTool(Name = "get_http_port"),
     Description("Get the HTTP port of the shikigami server. Pass this port to Shikigami.Runner via --mcp-port so shikigami can connect.")]
    public string GetHttpPort()
    {
        return JsonSerializer.Serialize(new { port = _state.HttpPort });
    }

    [McpServerTool(Name = "list_agents"),
     Description("Get list of all active agents (ID, name, type, task).")]
    public string ListAgents()
    {
        var result = _state.Agents.Values
            .Where(a => a.Active)
            .Select(a => new { id = a.Id, name = a.Name, agent_type = a.AgentType, task = a.Task })
            .ToList();
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "get_agent_state"),
     Description("Get recorded state of a specific agent by ID (status, current_step, metadata).")]
    public string GetAgentState(string agent_id)
    {
        if (!_state.Agents.TryGetValue(agent_id, out var a))
            return JsonSerializer.Serialize(new { error = "Agent not found" });
        return JsonSerializer.Serialize(new
        {
            id = a.Id, name = a.Name, active = a.Active,
            status = a.Status, current_step = a.CurrentStep, metadata = a.Metadata,
        });
    }

    [McpServerTool(Name = "send_message"),
     Description("Send a message to an agent by ID. Sender is automatically 'lead'. Returns error if recipient not found.")]
    public string SendMessage(string recipient_id, string text)
    {
        var msg = new MessageRecord { SenderId = "lead", Text = text };

        if (recipient_id == "lead")
        {
            _state.ToTrash(msg, recipient_id, "rejected");
            return JsonSerializer.Serialize(new { error = "Cannot send message to yourself" });
        }
        if (!_state.Agents.TryGetValue(recipient_id, out var recip) || !recip.Active)
        {
            _state.ToTrash(msg, recipient_id, "rejected");
            return JsonSerializer.Serialize(new { error = "Recipient not found" });
        }

        var queue = _state.Queues.GetOrAdd(recipient_id, _ => new List<MessageRecord>());
        lock (queue) queue.Add(msg);
        return JsonSerializer.Serialize(new { ok = true });
    }

    [McpServerTool(Name = "check_messages"),
     Description("Get (and delete) all incoming messages for the lead. Returns array of {sender_id, text, timestamp}.")]
    public string CheckMessages()
    {
        List<MessageRecord> messages;
        if (_state.Queues.TryGetValue("lead", out var queue))
        {
            lock (queue)
            {
                messages = new List<MessageRecord>(queue);
                queue.Clear();
            }
        }
        else
        {
            messages = new();
        }
        foreach (var msg in messages)
            _state.ToTrash(msg, "lead", "read");
        return JsonSerializer.Serialize(messages);
    }

    [McpServerTool(Name = "list_prompts"),
     Description("List all stored prompts with their IDs and creation times (for debugging).")]
    public string ListPrompts()
    {
        var result = _state.Prompts.Values
            .Select(p => new { id = p.Id, text_preview = p.Text[..Math.Min(100, p.Text.Length)], created_at = p.CreatedAt })
            .ToList();
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "get_agent_result"),
     Description("Get the final result of a completed agent. Returns the agent's output text. Works even after agent is inactive.")]
    public string GetAgentResult(string agent_id)
    {
        if (!_state.Agents.TryGetValue(agent_id, out var a))
            return JsonSerializer.Serialize(new { error = "Agent not found" });
        if (a.Result == null)
            return JsonSerializer.Serialize(new { error = "No result yet", status = a.Status });
        return JsonSerializer.Serialize(new { id = a.Id, name = a.Name, status = a.Status, result = a.Result });
    }

    [McpServerTool(Name = "get_agent_log"),
     Description("Get the event log of an agent (tool calls, model init, result metadata). For debugging.")]
    public string GetAgentLog(string agent_id)
    {
        if (!_state.Agents.TryGetValue(agent_id, out var a))
            return JsonSerializer.Serialize(new { error = "Agent not found" });
        if (a.EventLog == null)
            return JsonSerializer.Serialize(new { error = "No event log yet", status = a.Status });
        return JsonSerializer.Serialize(a.EventLog);
    }

    [McpServerTool(Name = "get_trash"),
     Description("Get the trash bin — all read, rejected, and purged messages (for debugging). Returns array sorted newest first.")]
    public string GetTrash(int last_n = 50)
    {
        var items = _state.Trash.ToArray();
        var result = items.TakeLast(last_n).Reverse().ToList();
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "get_total_cost"),
     Description("Get total cost across all shikigami and per-agent breakdown. Returns {total_cost_usd, agents: [{id, name, cost_usd}]}.")]
    public string GetTotalCost()
    {
        var agents = _state.Agents.Values
            .Where(a => a.CostUsd > 0)
            .Select(a => new { id = a.Id, name = a.Name, cost_usd = a.CostUsd })
            .ToList<object>();

        foreach (var pool in _state.Pools.Values)
        {
            foreach (var (aid, a) in pool.Agents)
            {
                if (a.CostUsd > 0)
                    agents.Add(new { id = aid, name = $"[{pool.Name}] {a.AgentType}", cost_usd = a.CostUsd });
            }
        }

        return JsonSerializer.Serialize(new { total_cost_usd = _state.TotalCost, agents });
    }

    [McpServerTool(Name = "create_agent_with_prompt"),
     Description("Create a shikigami: store prompt, launch Runner, return agent_id. " +
                  "One tool call replaces: generate ID + send_prompt + get_http_port + launch script. " +
                  "After this, start a background poller for the returned agent_id.")]
    public string CreateAgentWithPrompt(
        string prompt,
        string agent_name = "",
        string model = "",
        string tools = "",
        string workdir = "")
    {
        var result = _launcher.LaunchPromptAgent(prompt, agent_name, model, tools, workdir);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "create_tasks"),
     Description("Create a POOL of tasks with dependencies AND auto-launch agents. " +
                  "tasks_json is a JSON array of {id, title, description, agent_type, depends_on}. " +
                  "pool_name is a short human-readable label for the pool. " +
                  "Returns pool_id for monitoring. This is a one-shot call — agents work autonomously.")]
    public string CreateTasks(string tasks_json, string pool_name = "", string workdir = "")
    {
        List<Dictionary<string, object>> tasks;
        try
        {
            tasks = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(tasks_json)!;
        }
        catch (JsonException e)
        {
            return JsonSerializer.Serialize(new { error = $"Invalid JSON: {e.Message}" });
        }
        var result = _launcher.LaunchPool(tasks, pool_name, workdir);
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "list_pools"),
     Description("List all Horde pools with their statuses and task counts.")]
    public string ListPools()
    {
        var result = _state.Pools.Values.Select(pool =>
        {
            var tasks = pool.Tasks.Values.ToList();
            return new
            {
                pool_id = pool.Id,
                name = pool.Name,
                status = pool.Status,
                tasks_total = tasks.Count,
                tasks_completed = tasks.Count(t => t.Status == "completed"),
                tasks_failed = tasks.Count(t => t.Status == "failed"),
                tasks_pending = tasks.Count(t => t.Status == "pending"),
                tasks_in_progress = tasks.Count(t => t.Status == "in_progress"),
                agents_active = pool.Agents.Values.Count(a => a.Active),
                created_at = pool.CreatedAt,
            };
        }).ToList();
        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "list_pool_tasks"),
     Description("List all tasks in a specific pool with statuses, assignments, results, and dependencies.")]
    public string ListPoolTasks(string pool_id)
    {
        if (!_state.Pools.TryGetValue(pool_id, out var pool))
            return JsonSerializer.Serialize(new { error = "Pool not found" });

        var tasks = pool.TaskOrder.Select(tid =>
        {
            var t = pool.Tasks[tid];
            return new
            {
                id = t.Id, title = t.Title, agent_type = t.AgentType,
                status = t.Status, depends_on = t.DependsOn,
                assigned_to = t.AssignedTo, result = t.Result,
            };
        }).ToList();
        return JsonSerializer.Serialize(new { pool_id, status = pool.Status, tasks });
    }

    [McpServerTool(Name = "abort_pool"),
     Description("Abort a pool: stop accepting new task requests, mark pool as aborted. Running agents finish current task but get no new ones.")]
    public string AbortPool(string pool_id)
    {
        if (!_state.Pools.TryGetValue(pool_id, out var pool))
            return JsonSerializer.Serialize(new { error = "Pool not found" });
        pool.Status = "aborted";
        return JsonSerializer.Serialize(new { ok = true, pool_id, status = "aborted" });
    }

    [McpServerTool(Name = "update_task_status"),
     Description("Manually update task status in a pool with full resync. " +
                  "Valid statuses: pending, completed, failed. " +
                  "pending: clears all fields, auto-reopens direct cascade-failed dependents. " +
                  "completed: triggers unblocking + pool completion check. " +
                  "failed: triggers cascade failure + pool completion check.")]
    public string UpdateTaskStatus(string pool_id, string task_id, string new_status, string result = "")
    {
        if (!_state.Pools.TryGetValue(pool_id, out var pool))
            return JsonSerializer.Serialize(new { error = "Pool not found" });
        if (!pool.Tasks.TryGetValue(task_id, out var task))
            return JsonSerializer.Serialize(new { error = "Task not found" });
        if (new_status is not ("pending" or "completed" or "failed"))
            return JsonSerializer.Serialize(new { error = $"Invalid status: {new_status}. Must be: pending, completed, failed" });

        var oldStatus = task.Status;
        var now = DateTime.UtcNow.ToString("o");
        var reopened = new List<string>();
        var cascadeFailed = new List<string>();
        var unblocked = new List<string>();

        switch (new_status)
        {
            case "pending":
                task.Status = "pending";
                task.AssignedTo = null;
                task.Result = null;
                task.StartedAt = null;
                task.CompletedAt = null;
                reopened = _poolService.ReopenDirectCascadeDependents(pool, task_id);
                break;

            case "completed":
                task.Status = "completed";
                task.CompletedAt = now;
                if (!string.IsNullOrEmpty(result)) task.Result = result;
                foreach (var other in pool.Tasks.Values)
                {
                    if (other.Status == "pending" && other.DependsOn.Contains(task_id)
                        && other.DependsOn.All(d => pool.Tasks[d].Status == "completed"))
                        unblocked.Add(other.Id);
                }
                _poolService.CheckPoolCompletion(pool_id);
                break;

            case "failed":
                task.Status = "failed";
                task.CompletedAt = now;
                task.Result = string.IsNullOrEmpty(result) ? "Manually failed" : result;
                cascadeFailed = _poolService.CascadeFailure(pool, task_id);
                _poolService.CheckPoolCompletion(pool_id);
                break;
        }

        return JsonSerializer.Serialize(new
        {
            ok = true, task_id, old_status = oldStatus, new_status,
            reopened, cascade_failed = cascadeFailed, unblocked,
        });
    }

    [McpServerTool(Name = "check_pool_messages"),
     Description("Get all messages sent to 'lead' in a specific pool. Messages are consumed (popped) on retrieval.")]
    public string CheckPoolMessages(string pool_id)
    {
        if (!_state.Pools.TryGetValue(pool_id, out var pool))
            return JsonSerializer.Serialize(new { error = "Pool not found" });

        List<MessageRecord> messages;
        if (pool.Queues.TryGetValue("lead", out var queue))
        {
            lock (queue)
            {
                messages = new List<MessageRecord>(queue);
                queue.Clear();
            }
        }
        else
        {
            messages = new();
        }
        foreach (var msg in messages)
            ShikigamiState.PoolToTrash(pool, msg, "lead", "read");
        return JsonSerializer.Serialize(messages);
    }

    [McpServerTool(Name = "send_pool_message"),
     Description("Send message to an agent within a specific pool.")]
    public string SendPoolMessage(string pool_id, string recipient_id, string text)
    {
        if (!_state.Pools.TryGetValue(pool_id, out var pool))
            return JsonSerializer.Serialize(new { error = "Pool not found" });

        var msg = new MessageRecord { SenderId = "lead", Text = text };
        if (!pool.Agents.TryGetValue(recipient_id, out var ai) || !ai.Active)
        {
            ShikigamiState.PoolToTrash(pool, msg, recipient_id, "rejected");
            return JsonSerializer.Serialize(new { error = "Recipient not found in pool" });
        }

        var queue = pool.Queues.GetOrAdd(recipient_id, _ => new List<MessageRecord>());
        lock (queue) queue.Add(msg);
        return JsonSerializer.Serialize(new { ok = true });
    }
}
