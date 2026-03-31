using Shikigami.Core.Models;
using Shikigami.Core.State;

namespace Shikigami.Core.Services;

/// <summary>
/// Pool (Horde mode) business logic: task validation, creation, dependency resolution, cascade failure.
/// </summary>
public sealed class PoolService
{
    private readonly ShikigamiState _state;

    public PoolService(ShikigamiState state)
    {
        _state = state;
    }

    /// <summary>
    /// Validate a task batch. Returns error string or null if OK.
    /// </summary>
    public string? ValidateTasks(List<Dictionary<string, object>> tasksBatch)
    {
        var batchIds = new HashSet<string>();
        foreach (var task in tasksBatch)
        {
            var tid = task["id"].ToString()!;
            if (!batchIds.Add(tid))
                return $"Duplicate task ID in batch: '{tid}'";
        }

        foreach (var task in tasksBatch)
        {
            var deps = GetDependsOn(task);
            foreach (var depId in deps)
            {
                if (!batchIds.Contains(depId))
                    return $"Unknown dependency: '{depId}' in task '{task["id"]}'";
            }
        }

        // Cycle detection
        var graph = tasksBatch.ToDictionary(
            t => t["id"].ToString()!,
            t => GetDependsOn(t));

        var visited = new HashSet<string>();
        var inStack = new HashSet<string>();

        bool HasCycle(string node)
        {
            visited.Add(node);
            inStack.Add(node);
            if (graph.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    if (inStack.Contains(dep)) return true;
                    if (!visited.Contains(dep) && graph.ContainsKey(dep) && HasCycle(dep)) return true;
                }
            }
            inStack.Remove(node);
            return false;
        }

        foreach (var taskId in graph.Keys)
        {
            if (!visited.Contains(taskId) && HasCycle(taskId))
                return $"Cycle detected involving: [{string.Join(", ", inStack)}]";
        }

        return null;
    }

    /// <summary>
    /// Create a new pool from a validated task batch.
    /// </summary>
    public PoolRecord CreatePool(string poolId, List<Dictionary<string, object>> tasksBatch, string name = "")
    {
        var now = DateTime.UtcNow.ToString("o");
        var pool = new PoolRecord
        {
            Id = poolId,
            Name = string.IsNullOrEmpty(name) ? poolId : name,
            CreatedAt = now,
        };

        foreach (var t in tasksBatch)
        {
            var taskId = t["id"].ToString()!;
            pool.Tasks[taskId] = new TaskRecord
            {
                Id = taskId,
                Title = t["title"].ToString()!,
                Description = t["description"].ToString()!,
                AgentType = t["agent_type"].ToString()!,
                DependsOn = GetDependsOn(t),
                CreatedAt = now,
            };
            pool.TaskOrder.Add(taskId);
        }

        _state.Pools[poolId] = pool;
        return pool;
    }

    /// <summary>
    /// Atomically find and assign the first available task for a given agent type.
    /// Returns the assigned task, or null if none available.
    /// </summary>
    public TaskRecord? TryAssignTask(string poolId, string agentType, string agentId)
    {
        if (!_state.Pools.TryGetValue(poolId, out var pool)) return null;

        lock (pool.TaskOrder)
        {
            foreach (var taskId in pool.TaskOrder)
            {
                var task = pool.Tasks[taskId];
                if (task.Status != "pending") continue;
                if (task.AgentType != agentType) continue;
                if (!task.DependsOn.All(depId => pool.Tasks[depId].Status == "completed")) continue;

                task.Status = "in_progress";
                task.AssignedTo = agentId;
                task.StartedAt = DateTime.UtcNow.ToString("o");
                return task;
            }
        }
        return null;
    }

    /// <summary>
    /// Check if all tasks in pool are terminal. Update pool status if so.
    /// </summary>
    public bool CheckPoolCompletion(string poolId)
    {
        if (!_state.Pools.TryGetValue(poolId, out var pool)) return false;
        if (pool.Status != "in_progress") return pool.Status == "completed";

        var allDone = pool.Tasks.Values.All(t => t.Status is "completed" or "failed");
        if (allDone)
        {
            pool.Status = "completed";
            pool.CompletedAt = DateTime.UtcNow.ToString("o");
        }
        return allDone;
    }

    /// <summary>
    /// Cascade-fail all tasks that depend on a failed task (transitively).
    /// </summary>
    public List<string> CascadeFailure(PoolRecord pool, string failedTaskId)
    {
        var now = DateTime.UtcNow.ToString("o");
        var newlyFailed = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(failedTaskId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            foreach (var task in pool.Tasks.Values)
            {
                if (task.DependsOn.Contains(currentId) && task.Status == "pending")
                {
                    task.Status = "failed";
                    task.Result = $"Dependency failed: {failedTaskId}";
                    task.CompletedAt = now;
                    newlyFailed.Add(task.Id);
                    queue.Enqueue(task.Id);
                }
            }
        }
        return newlyFailed;
    }

    /// <summary>
    /// Re-open tasks that were cascade-failed and directly depend on the given task.
    /// </summary>
    public List<string> ReopenDirectCascadeDependents(PoolRecord pool, string taskId)
    {
        var reopened = new List<string>();
        foreach (var task in pool.Tasks.Values)
        {
            if (task.DependsOn.Contains(taskId)
                && task.Status == "failed"
                && (task.Result ?? "").StartsWith("Dependency failed:"))
            {
                task.Status = "pending";
                task.AssignedTo = null;
                task.Result = null;
                task.StartedAt = null;
                task.CompletedAt = null;
                reopened.Add(task.Id);
            }
        }
        return reopened;
    }

    private static List<string> GetDependsOn(Dictionary<string, object> task)
    {
        if (!task.TryGetValue("depends_on", out var val)) return new();
        if (val is List<string> list) return list;
        if (val is System.Text.Json.JsonElement elem && elem.ValueKind == System.Text.Json.JsonValueKind.Array)
            return elem.EnumerateArray().Select(e => e.GetString()!).ToList();
        return new();
    }
}
