using System.Text.Json;
using Shikigami.Core.Models;
using Shikigami.Core.Services;
using Shikigami.Core.State;

namespace Shikigami.Server.Http;

/// <summary>
/// Minimal API endpoints for prompt-mode shikigami: registration, state, messaging, results.
/// </summary>
public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app, ShikigamiState state, LaunchService launcher)
    {
        app.MapPost("/agents/register", async (HttpContext ctx) =>
        {
            var data = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            string[] required = ["prompt_id", "name", "task", "parent_id", "pid", "agent_type"];
            var missing = required.Where(f => !data.TryGetProperty(f, out _)).ToList();
            if (missing.Count > 0)
                return Results.Json(new { error = $"Missing fields: {string.Join(", ", missing)}" }, statusCode: 400);

            var agentId = data.GetProperty("prompt_id").GetString()!;
            if (state.Agents.TryGetValue(agentId, out var existing) && existing.Active)
                return Results.Json(new { error = $"Agent with id '{agentId}' is already active" }, statusCode: 409);

            var agent = new AgentRecord
            {
                Id = agentId,
                Name = data.GetProperty("name").GetString()!,
                Task = data.GetProperty("task").GetString()!,
                ParentId = data.GetProperty("parent_id").GetString()!,
                Pid = data.GetProperty("pid").GetInt32(),
                AgentType = data.GetProperty("agent_type").GetString()!,
            };
            state.Agents[agentId] = agent;
            state.Queues[agentId] = new MessageQueue();

            return Results.Json(new { id = agentId });
        });

        app.MapPost("/agents/{id}/unregister", (string id) =>
        {
            if (!state.Agents.ContainsKey(id))
                return Results.Json(new { error = "Agent not found" }, statusCode: 404);
            state.MarkDead(id);
            return Results.Json(new { ok = true });
        });

        app.MapPut("/agents/{id}/state", async (string id, HttpContext ctx) =>
        {
            if (!state.Agents.TryGetValue(id, out var agent) || !agent.Active)
                return Results.Json(new { error = "Agent not found or inactive" }, statusCode: 404);

            var data = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            var prevState = agent.GetState();
            if (data.TryGetProperty("current_step", out var cs)) agent.CurrentStep = cs.GetString();

            // Auto-notify parent on transitions to terminal/idle/taken canonical states
            var newState = agent.GetState();
            if (newState != prevState
                && newState is "completed" or "failed" or "idle" or "taken")
            {
                state.NotifyParent(agent);
            }

            return Results.Json(new { ok = true });
        });

        app.MapGet("/agents", () =>
        {
            var result = state.Agents.Values
                .Where(a => a.Active)
                .Select(a => new { id = a.Id, name = a.Name, agent_type = a.AgentType })
                .ToList();
            return Results.Json(result);
        });

        app.MapPost("/messages/send", async (HttpContext ctx) =>
        {
            var data = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            var senderId = data.GetProperty("sender_id").GetString()!;
            var recipientId = data.GetProperty("recipient_id").GetString()!;
            var text = data.GetProperty("text").GetString()!;

            var msg = new MessageRecord { SenderId = senderId, Text = text };

            if (recipientId != "lead"
                && (!state.Agents.TryGetValue(recipientId, out var recip) || !recip.Active))
            {
                state.ToTrash(msg, recipientId, "rejected");
                return Results.Json(new { error = "Recipient not found" }, statusCode: 404);
            }

            var queue = state.Queues.GetOrAdd(recipientId, _ => new MessageQueue());
            queue.Enqueue(msg);
            return Results.Json(new { ok = true });
        });

        app.MapGet("/messages/{agentId}", (string agentId) =>
        {
            if (agentId != "lead" && (!state.Agents.TryGetValue(agentId, out var a) || !a.Active))
                return Results.Json(new { error = "Agent not found" }, statusCode: 404);

            var messages = state.Queues.TryGetValue(agentId, out var queue)
                ? queue.DrainAll()
                : new List<MessageRecord>();

            foreach (var msg in messages)
                state.ToTrash(msg, agentId, "read");
            return Results.Json(messages);
        });

        app.MapGet("/agents/{id}/state", (string id) =>
        {
            if (!state.Agents.TryGetValue(id, out var a))
                return Results.Json(new { error = "Agent not found" }, statusCode: 404);
            return Results.Json(new { id = a.Id, name = a.Name, agent_type = a.AgentType, current_step = a.CurrentStep });
        });

        app.MapGet("/agents/{id}/result", (string id) =>
        {
            if (!state.Agents.TryGetValue(id, out var a))
                return Results.Json(new { error = "Agent not found" }, statusCode: 404);
            if (a.Result == null)
                return Results.Json(new { error = "No result yet", current_step = a.CurrentStep }, statusCode: 404);
            return Results.Json(new { id = a.Id, name = a.Name, current_step = a.CurrentStep, result = a.Result });
        });

        app.MapPut("/agents/{id}/result", async (string id, HttpContext ctx) =>
        {
            if (!state.Agents.TryGetValue(id, out var agent))
                return Results.Json(new { error = "Agent not found" }, statusCode: 404);

            var data = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            if (data.TryGetProperty("result", out var r)) agent.Result = r.GetString();
            if (data.TryGetProperty("event_log", out var el)) agent.EventLog = el;

            return Results.Json(new { ok = true });
        });

        app.MapPut("/agents/{id}/cost", async (string id, HttpContext ctx) =>
        {
            var data = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            var cost = data.GetProperty("total_cost_usd").GetDouble();

            // Try regular agents first
            if (state.Agents.TryGetValue(id, out var agent))
            {
                state.UpdateAgentCost(agent, cost);
                return Results.Json(new { ok = true, agent_cost = cost, total_cost = state.TotalCost });
            }

            // Try pool agents
            foreach (var pool in state.Pools.Values)
            {
                if (pool.Agents.TryGetValue(id, out var poolAgent))
                {
                    state.UpdatePoolAgentCost(poolAgent, cost);
                    return Results.Json(new { ok = true, agent_cost = cost, total_cost = state.TotalCost });
                }
            }

            return Results.Json(new { error = "Agent not found" }, statusCode: 404);
        });

        app.MapGet("/prompts/{promptId}", (string promptId) =>
        {
            if (!state.Prompts.TryGetValue(promptId, out var prompt))
                return Results.Json(new { error = "Prompt not found" }, statusCode: 404);
            return Results.Json(new { id = prompt.Id, text = prompt.Text, created_at = prompt.CreatedAt });
        });

        app.MapGet("/agents/{id}/wait", async (string id, HttpContext ctx) =>
        {
            var timeout = int.TryParse(ctx.Request.Query["timeout"].FirstOrDefault(), out var t) ? t : 1800;
            var elapsed = 0;
            const int interval = 2;

            while (elapsed < timeout)
            {
                if (!state.Agents.TryGetValue(id, out var agent))
                    return Results.Json(new { agent_id = id, current_step = "dead" });

                if (!agent.Active)
                    return Results.Json(new { agent_id = id, current_step = agent.CurrentStep });

                if (agent.GetState() is "completed" or "failed" or "idle" or "taken")
                    return Results.Json(new { agent_id = id, current_step = agent.CurrentStep });

                await Task.Delay(interval * 1000, ctx.RequestAborted);
                elapsed += interval;
            }
            return Results.Json(new { agent_id = id, current_step = "timeout" }, statusCode: 408);
        });

        app.MapGet("/messages/{agentId}/wait", async (string agentId, HttpContext ctx) =>
        {
            var timeout = int.TryParse(ctx.Request.Query["timeout"].FirstOrDefault(), out var t) ? t : 1800;
            if (agentId != "lead" && (!state.Agents.TryGetValue(agentId, out var a) || !a.Active))
                return Results.Json(new { error = "Agent not found" }, statusCode: 404);

            var deadline = DateTime.UtcNow.AddSeconds(timeout);
            while (DateTime.UtcNow < deadline)
            {
                if (state.Queues.TryGetValue(agentId, out var queue) && queue.Count > 0)
                {
                    var messages = queue.DrainAll();
                    foreach (var msg in messages)
                        state.ToTrash(msg, agentId, "read");
                    return Results.Json(messages);
                }
                try { await Task.Delay(500, ctx.RequestAborted); }
                catch (OperationCanceledException) { return Results.Json(Array.Empty<MessageRecord>()); }
            }
            return Results.Json(Array.Empty<MessageRecord>());
        });

        // HTTP mirrors of MCP tools
        app.MapPost("/agents/create", async (HttpContext ctx) =>
        {
            var data = await ctx.Request.ReadFromJsonAsync<JsonElement>();
            if (!data.TryGetProperty("prompt", out _))
                return Results.Json(new { error = "Missing required field: prompt" }, statusCode: 400);
            if (!data.TryGetProperty("lead_id", out _))
                return Results.Json(new { error = "Missing required field: lead_id" }, statusCode: 400);

            var result = launcher.LaunchPromptAgent(
                prompt: data.GetProperty("prompt").GetString()!,
                agentName: data.TryGetProperty("agent_name", out var an) ? an.GetString()! : "",
                model: data.TryGetProperty("model", out var m) ? m.GetString()! : "",
                tools: data.TryGetProperty("tools", out var t) ? t.GetString()! : "",
                workdir: data.TryGetProperty("workdir", out var w) ? w.GetString()! : "",
                leadId: data.GetProperty("lead_id").GetString()!);

            var status = result.ContainsKey("error") ? 400 : 200;
            return Results.Json(result, statusCode: status);
        });
    }
}
