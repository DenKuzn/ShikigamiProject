using System.Text.Json;
using Shikigami.Core.Models;
using Shikigami.Core.Services;
using Shikigami.Core.State;

namespace Shikigami.Server.Http;

/// <summary>
/// Minimal API endpoints for Horde pools: task request/complete/fail, agent management, messaging.
/// </summary>
public static class PoolEndpoints
{
    public static void MapPoolEndpoints(this WebApplication app, ShikigamiState state,
        PoolService poolService, LaunchService launcher)
    {
        app.MapPost("/pools/create", async (HttpContext ctx) =>
        {
            var data = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            if (!data.TryGetProperty("tasks", out var tasksElem))
                return Results.Json(new { error = "Missing required field: tasks" }, statusCode: 400);
            if (!data.TryGetProperty("lead_id", out _))
                return Results.Json(new { error = "Missing required field: lead_id" }, statusCode: 400);

            var tasks = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(tasksElem.GetRawText())!;
            var result = launcher.LaunchPool(
                tasksBatch: tasks,
                poolName: data.TryGetProperty("pool_name", out var pn) ? pn.GetString()! : "",
                workdir: data.TryGetProperty("workdir", out var w) ? w.GetString()! : "",
                leadId: data.GetProperty("lead_id").GetString()!);

            var status = result.ContainsKey("error") ? 400 : 200;
            return Results.Json(result, statusCode: status);
        });

        app.MapGet("/pools/{poolId}/tasks", (string poolId) =>
        {
            if (!state.Pools.TryGetValue(poolId, out var pool))
                return Results.Json(new { error = "Pool not found" }, statusCode: 404);

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
            return Results.Json(new { pool_id = poolId, status = pool.Status, tasks });
        });

        app.MapGet("/pools/{poolId}/tasks/request", (string poolId, HttpContext ctx) =>
        {
            if (!state.Pools.TryGetValue(poolId, out var pool))
                return Results.Json(new { error = "Pool not found" }, statusCode: 404);
            if (pool.Status == "aborted")
                return Results.Json(new { error = "Pool aborted" }, statusCode: 410);

            var agentType = ctx.Request.Query["agent_type"].FirstOrDefault() ?? "";
            var agentId = ctx.Request.Query["agent_id"].FirstOrDefault() ?? "";

            var task = poolService.GetAvailableTask(poolId, agentType);
            if (task != null)
            {
                task.Status = "in_progress";
                task.AssignedTo = agentId;
                task.StartedAt = DateTime.UtcNow.ToString("o");
                var remaining = pool.Tasks.Values.Count(t => t.Status == "pending");
                return Results.Json(new { task, remaining });
            }

            var allTerminal = pool.Tasks.Values.All(t => t.Status is "completed" or "failed");
            if (allTerminal)
                return Results.Json(new { task = (object?)null, all_done = true });

            var pendingTasks = pool.Tasks.Values.Where(t => t.Status == "pending").ToList();
            var blockedTypes = pendingTasks.Select(t => t.AgentType).Distinct().OrderBy(t => t).ToList();
            return Results.Json(new
            {
                task = (object?)null,
                all_done = false,
                reason = "blocked",
                blocked_count = pendingTasks.Count,
                blocked_agent_types = blockedTypes,
            });
        });

        app.MapPut("/pools/{poolId}/tasks/{taskId}/complete", async (string poolId, string taskId, HttpContext ctx) =>
        {
            if (!state.Pools.TryGetValue(poolId, out var pool))
                return Results.Json(new { error = "Pool not found" }, statusCode: 404);
            if (!pool.Tasks.TryGetValue(taskId, out var task))
                return Results.Json(new { error = "Task not found" }, statusCode: 404);

            var data = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            var agentId = data.GetProperty("agent_id").GetString()!;
            if (task.AssignedTo != agentId)
                return Results.Json(new { error = "Task not assigned to this agent" }, statusCode: 403);

            task.Status = "completed";
            task.Result = data.TryGetProperty("result", out var r) ? r.GetString() : "";
            task.CompletedAt = DateTime.UtcNow.ToString("o");

            var unblocked = new List<string>();
            foreach (var other in pool.Tasks.Values)
            {
                if (other.Status == "pending" && other.DependsOn.Contains(taskId)
                    && other.DependsOn.All(d => pool.Tasks[d].Status == "completed"))
                    unblocked.Add(other.Id);
            }

            poolService.CheckPoolCompletion(poolId);
            return Results.Json(new { ok = true, unblocked });
        });

        app.MapPut("/pools/{poolId}/tasks/{taskId}/fail", async (string poolId, string taskId, HttpContext ctx) =>
        {
            if (!state.Pools.TryGetValue(poolId, out var pool))
                return Results.Json(new { error = "Pool not found" }, statusCode: 404);
            if (!pool.Tasks.TryGetValue(taskId, out var task))
                return Results.Json(new { error = "Task not found" }, statusCode: 404);

            var data = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            var agentId = data.GetProperty("agent_id").GetString()!;
            if (task.AssignedTo != agentId)
                return Results.Json(new { error = "Task not assigned to this agent" }, statusCode: 403);

            task.Status = "failed";
            task.Result = data.TryGetProperty("reason", out var r) ? r.GetString() : "Unknown failure";
            task.CompletedAt = DateTime.UtcNow.ToString("o");

            var cascadeFailed = poolService.CascadeFailure(pool, taskId);
            poolService.CheckPoolCompletion(poolId);
            return Results.Json(new { ok = true, cascade_failed = cascadeFailed });
        });

        app.MapPost("/pools/{poolId}/agents/register", async (string poolId, HttpContext ctx) =>
        {
            if (!state.Pools.TryGetValue(poolId, out var pool))
                return Results.Json(new { error = "Pool not found" }, statusCode: 404);

            var data = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            var agentId = data.GetProperty("agent_id").GetString()!;

            pool.Agents[agentId] = new PoolAgentInfo
            {
                AgentType = data.GetProperty("agent_type").GetString()!,
                Pid = data.GetProperty("pid").GetInt32(),
            };
            pool.Queues.GetOrAdd(agentId, _ => new List<MessageRecord>());

            return Results.Json(new { ok = true });
        });

        app.MapPut("/pools/{poolId}/agents/{agentId}/state", async (string poolId, string agentId, HttpContext ctx) =>
        {
            if (!state.Pools.TryGetValue(poolId, out var pool))
                return Results.Json(new { error = "Pool not found" }, statusCode: 404);
            if (!pool.Agents.TryGetValue(agentId, out var agentInfo))
                return Results.Json(new { error = "Agent not found in pool" }, statusCode: 404);

            var data = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            if (data.TryGetProperty("state", out var s)) agentInfo.State = s.GetString()!;
            if (data.TryGetProperty("detail", out var d)) agentInfo.StateDetail = d.GetString()!;

            return Results.Json(new { ok = true });
        });

        app.MapDelete("/pools/{poolId}/agents/{agentId}", (string poolId, string agentId) =>
        {
            if (!state.Pools.TryGetValue(poolId, out var pool))
                return Results.Json(new { error = "Pool not found" }, statusCode: 404);
            if (!pool.Agents.TryGetValue(agentId, out var agentInfo))
                return Results.Json(new { error = "Agent not found in pool" }, statusCode: 404);

            agentInfo.Active = false;
            agentInfo.State = "completed";
            if (pool.Queues.TryRemove(agentId, out var msgs))
                foreach (var msg in msgs)
                    ShikigamiState.PoolToTrash(pool, msg, agentId, "agent_unregistered");

            return Results.Json(new { ok = true });
        });

        app.MapPost("/pools/{poolId}/messages/send", async (string poolId, HttpContext ctx) =>
        {
            if (!state.Pools.TryGetValue(poolId, out var pool))
                return Results.Json(new { error = "Pool not found" }, statusCode: 404);

            var data = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            var senderId = data.GetProperty("sender_id").GetString()!;
            var recipientId = data.GetProperty("recipient_id").GetString()!;
            var msg = new MessageRecord { SenderId = senderId, Text = data.GetProperty("text").GetString()! };

            if (recipientId == "all")
            {
                foreach (var (aid, ai) in pool.Agents)
                {
                    if (aid != senderId && ai.Active)
                    {
                        var q = pool.Queues.GetOrAdd(aid, _ => new List<MessageRecord>());
                        lock (q) q.Add(new MessageRecord { SenderId = senderId, Text = msg.Text });
                    }
                }
                var leadQ = pool.Queues.GetOrAdd("lead", _ => new List<MessageRecord>());
                lock (leadQ) leadQ.Add(new MessageRecord { SenderId = senderId, Text = msg.Text });
            }
            else if (recipientId == "lead")
            {
                var q = pool.Queues.GetOrAdd("lead", _ => new List<MessageRecord>());
                lock (q) q.Add(msg);
            }
            else if (pool.Agents.ContainsKey(recipientId))
            {
                var q = pool.Queues.GetOrAdd(recipientId, _ => new List<MessageRecord>());
                lock (q) q.Add(msg);
            }
            else
            {
                ShikigamiState.PoolToTrash(pool, msg, recipientId, "rejected");
                return Results.Json(new { error = "Recipient not found in pool" }, statusCode: 404);
            }

            return Results.Json(new { ok = true });
        });

        app.MapGet("/pools/{poolId}/messages/check", (string poolId, HttpContext ctx) =>
        {
            if (!state.Pools.TryGetValue(poolId, out var pool))
                return Results.Json(new { error = "Pool not found" }, statusCode: 404);

            var agentId = ctx.Request.Query["agent_id"].FirstOrDefault() ?? "";
            List<MessageRecord> messages;
            if (pool.Queues.TryGetValue(agentId, out var queue))
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
                ShikigamiState.PoolToTrash(pool, msg, agentId, "read");
            return Results.Json(new { messages });
        });
    }
}
