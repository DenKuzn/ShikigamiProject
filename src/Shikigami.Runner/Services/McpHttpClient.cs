using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Shikigami.Runner.Services;

/// <summary>
/// HTTP client for communicating with the ShikigamiMCP server.
/// Equivalent to Python mcp_client.py.
/// </summary>
public sealed class McpHttpClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private int? _port;

    public string? AgentId { get; set; }
    public bool Active => _port != null;

    public McpHttpClient(int? port)
    {
        _port = port;
    }

    public async Task ValidatePortAsync()
    {
        if (_port == null) return;
        try
        {
            await _http.GetAsync($"http://127.0.0.1:{_port}/agents");
        }
        catch
        {
            _port = null;
        }
    }

    public int? Port => _port;

    public async Task<JsonElement?> RequestAsync(string method, string path, object? data = null)
    {
        if (_port == null) return null;
        var url = $"http://127.0.0.1:{_port}{path}";
        try
        {
            HttpResponseMessage resp;
            var content = data != null
                ? new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json")
                : null;

            resp = method.ToUpperInvariant() switch
            {
                "GET" => await _http.GetAsync(url),
                "POST" => await _http.PostAsync(url, content),
                "PUT" => await _http.PutAsync(url, content),
                "DELETE" => await _http.DeleteAsync(url),
                _ => throw new ArgumentException($"Unknown method: {method}"),
            };

            var body = await resp.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch
        {
            return null;
        }
    }

    // ── Prompt mode ──

    public async Task<string?> RegisterAsync(string promptId, string name, string task, int pid, string parentId = "lead")
    {
        if (_port == null || string.IsNullOrEmpty(promptId)) return null;
        var resp = await RequestAsync("POST", "/agents/register", new
        {
            prompt_id = promptId,
            name,
            task,
            parent_id = parentId,
            pid,
            agent_type = "shikigami",
        });
        if (resp?.TryGetProperty("id", out var idProp) == true)
        {
            AgentId = idProp.GetString();
            return AgentId;
        }
        return null;
    }

    public async Task UnregisterAsync()
    {
        if (AgentId != null) await RequestAsync("POST", $"/agents/{AgentId}/unregister");
    }

    public async Task UpdateStateAsync(string currentStep)
    {
        if (AgentId == null) return;
        await RequestAsync("PUT", $"/agents/{AgentId}/state", new { current_step = currentStep });
    }

    public async Task SubmitLogAsync(object cleanContext, string lastOutput)
    {
        if (AgentId == null) return;
        await RequestAsync("PUT", $"/agents/{AgentId}/result", new { result = lastOutput, event_log = cleanContext });
    }

    public async Task SubmitCostAsync(double totalCost)
    {
        if (AgentId == null) return;
        await RequestAsync("PUT", $"/agents/{AgentId}/cost", new { total_cost_usd = totalCost });
    }

    public async Task<List<JsonElement>> CheckMessagesAsync(string promptId)
    {
        if (_port == null || string.IsNullOrEmpty(promptId)) return new();
        var resp = await RequestAsync("GET", $"/messages/{promptId}");
        if (resp is { ValueKind: JsonValueKind.Array } arr)
            return arr.EnumerateArray().ToList();
        return new();
    }

    // ── Horde mode ──

    public async Task PoolRegisterAsync(string poolId, string agentId, string agentType)
    {
        await RequestAsync("POST", $"/pools/{poolId}/agents/register", new
        {
            agent_id = agentId,
            agent_type = agentType,
            pid = Environment.ProcessId,
        });
    }

    public async Task PoolUpdateStateAsync(string poolId, string agentId, string agentState, string detail)
    {
        await RequestAsync("PUT", $"/pools/{poolId}/agents/{agentId}/state", new { state = agentState, detail });
    }

    public async Task PoolUnregisterAsync(string poolId, string agentId)
    {
        await RequestAsync("DELETE", $"/pools/{poolId}/agents/{agentId}");
    }

    public Task<JsonElement?> RequestTaskAsync(string poolId, string agentType, string agentId) =>
        RequestAsync("GET", $"/pools/{poolId}/tasks/request?agent_type={agentType}&agent_id={agentId}");

    public Task<JsonElement?> CompleteTaskAsync(string poolId, string taskId, string agentId, string result) =>
        RequestAsync("PUT", $"/pools/{poolId}/tasks/{taskId}/complete", new { agent_id = agentId, result });

    public Task<JsonElement?> FailTaskAsync(string poolId, string taskId, string agentId, string reason) =>
        RequestAsync("PUT", $"/pools/{poolId}/tasks/{taskId}/fail", new { agent_id = agentId, reason });

    public async Task<List<JsonElement>> PoolCheckMessagesAsync(string poolId, string agentId)
    {
        var resp = await RequestAsync("GET", $"/pools/{poolId}/messages/check?agent_id={agentId}");
        if (resp?.TryGetProperty("messages", out var msgs) == true && msgs.ValueKind == JsonValueKind.Array)
            return msgs.EnumerateArray().ToList();
        return new();
    }
}
