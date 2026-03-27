using System.IO;
using System.Text.Json;

namespace Shikigami.Runner.Services;

/// <summary>
/// Builds prompts for each CLI pass, including MCP header, communication directives,
/// and conversation history for multi-pass iterations.
///
/// Prompt templates are loaded from external files next to the executable,
/// allowing manual editing without recompilation.
/// </summary>
public sealed class PromptBuilder
{
    private readonly string _originalPrompt;
    private readonly int? _mcpPort;
    private readonly string? _promptId;
    private readonly bool _skipCommDirective;
    private readonly string _leadId;

    // Loaded from files at construction time
    private readonly string _commDirective;
    private readonly string _hordeCommDirectiveTemplate;
    private readonly string _mcpHeaderTemplate;
    private readonly string _poolMcpHeaderTemplate;

    public PromptBuilder(string originalPrompt, int? mcpPort = null, string? promptId = null,
                         bool skipCommDirective = false, string leadId = "lead")
    {
        _originalPrompt = originalPrompt;
        _mcpPort = mcpPort;
        _promptId = promptId;
        _skipCommDirective = skipCommDirective;
        _leadId = leadId;

        var baseDir = AppContext.BaseDirectory;
        _commDirective = LoadTemplate(baseDir, "prompt_comm.txt", DefaultCommDirective);
        _hordeCommDirectiveTemplate = LoadTemplate(baseDir, "prompt_horde_comm.txt", DefaultHordeCommDirective);
        _mcpHeaderTemplate = LoadTemplate(baseDir, "prompt_mcp_header.txt", DefaultMcpHeader);
        _poolMcpHeaderTemplate = LoadTemplate(baseDir, "prompt_pool_mcp_header.txt", DefaultPoolMcpHeader);
    }

    /// <summary>
    /// Build the MCP connection header for prompt-mode agents.
    /// </summary>
    private string McpHeader()
    {
        if (_mcpPort == null || string.IsNullOrEmpty(_promptId))
            return "";

        return _mcpHeaderTemplate
            .Replace("{port}", _mcpPort.ToString())
            .Replace("{agent_id}", _promptId)
            .Replace("{lead_id}", _leadId);
    }

    /// <summary>
    /// The full prompt as shown before the first pass (for display in the log).
    /// </summary>
    public string FullPromptDisplay()
    {
        var suffix = _skipCommDirective ? "" : _commDirective;
        return McpHeader() + $"## Your task:\n{_originalPrompt}" + suffix;
    }

    /// <summary>
    /// Build the prompt for a given pass iteration (prompt mode).
    /// </summary>
    public string Build(int iteration, List<Dictionary<string, object>>? allEvents = null)
    {
        var mcp = McpHeader();
        var suffix = _skipCommDirective ? "" : _commDirective;

        if (iteration == 1 || allEvents == null || allEvents.Count == 0)
            return mcp + $"## Your task:\n{_originalPrompt}" + suffix;

        // Extract user inputs and messages — present them prominently
        var interactions = new List<string>();
        foreach (var evt in allEvents)
        {
            var type = evt.TryGetValue("type", out var t) ? t.ToString() : null;
            var text = evt.TryGetValue("text", out var tx) ? tx.ToString() : null;
            if (string.IsNullOrEmpty(text)) continue;
            if (type == "user_input")
                interactions.Add($"- User answered: {text}");
            else if (type == "mcp_message")
                interactions.Add($"- {text}");
        }

        var parts = new List<string>
        {
            mcp + $"## Your task:\n{_originalPrompt}",
        };

        if (interactions.Count > 0)
        {
            parts.Add("## New Information (IMPORTANT — read and act on this)\n" +
                       string.Join("\n", interactions));
            parts.Add("Continue from where you left off. Act on the new information above. " +
                       "Do NOT repeat questions that have been answered. Do NOT re-read files you already read.");
        }
        else
        {
            parts.Add("Continue from where you left off. " +
                       "Do NOT re-ask answered questions or re-read files you already have in history.");
        }

        if (!_skipCommDirective)
            parts.Add(suffix);
        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// Build the full prompt for a Horde task.
    /// </summary>
    public static string BuildTaskPrompt(
        string title, string description,
        int mcpPort, string agentId, string poolId, string leadId = "lead")
    {
        var baseDir = AppContext.BaseDirectory;
        var hordeTemplate = LoadTemplate(baseDir, "prompt_horde_comm.txt", DefaultHordeCommDirective);
        var poolTemplate = LoadTemplate(baseDir, "prompt_pool_mcp_header.txt", DefaultPoolMcpHeader);

        var commDirective = hordeTemplate.Replace("{title}", title);
        var mcpHeader = poolTemplate
            .Replace("{port}", mcpPort.ToString())
            .Replace("{agent_id}", agentId)
            .Replace("{lead_id}", leadId)
            .Replace("{pool_id}", poolId);

        return $"{commDirective}\n{mcpHeader}\n## Task: {title}\n\n{description}";
    }

    // ── File loading ──

    private static string LoadTemplate(string baseDir, string fileName, string fallback)
    {
        var path = Path.Combine(baseDir, fileName);
        if (File.Exists(path))
        {
            try { return File.ReadAllText(path); }
            catch { /* fall through */ }
        }
        return fallback;
    }

    // ── Default templates (used if files are missing) ──

    private const string DefaultCommDirective =
        "\n\n## Communication\n" +
        "If you need user input, end your response with:\n" +
        "USER_INPUT_REQUIRED: <your question>\n\n" +
        "If you completed your task but want to remain available for follow-up " +
        "instructions (idle mode), end your response with:\n" +
        "AGENT_IDLE\n\n" +
        "If you completed your task and no follow-up is needed, " +
        "end your response with:\n" +
        "AGENT_COMPLETED\n\n" +
        "CRITICAL: Every final response MUST end with one of these markers: " +
        "USER_INPUT_REQUIRED, AGENT_IDLE, or AGENT_COMPLETED. " +
        "A response without a marker is treated as an error.\n";

    private const string DefaultHordeCommDirective =
        "\n\n## Communication\n" +
        "If you need user input, end your response with:\n" +
        "USER_INPUT_REQUIRED: <your question>\n\n" +
        "When you're done with the task, end your response with:\n" +
        "TASK_COMPLETED\n\n" +
        "If you CANNOT complete the task, end your response with:\n" +
        "TASK_FAILED: <reason>\n\n" +
        "CRITICAL: Every final response MUST end with one of these markers: " +
        "USER_INPUT_REQUIRED, TASK_COMPLETED, or TASK_FAILED. " +
        "A response without a marker is treated as an error.\n\n" +
        "## Task Completion Report\n" +
        "Before the TASK_COMPLETED marker, write a brief result summary:\n" +
        "- If fully done: 'Task \"{title}\" completed fully.' then TASK_COMPLETED\n" +
        "- If done with deviations: 'Task \"{title}\" completed. " +
        "Deviations: [desc]. Additions: [desc].' then TASK_COMPLETED\n";

    private const string DefaultMcpHeader =
        "## MCP Connection\n" +
        "- **Port**: {port}\n" +
        "- **Your Agent ID**: {agent_id}\n" +
        "- **Your Lead ID**: {lead_id}\n\n" +
        "### Send message\n" +
        "CRITICAL: Do NOT use curl — it breaks Unicode on Windows. Use PowerShell instead.\n" +
        "Set `recipient_id` to `{lead_id}` (your lead) or another agent's ID. Replace YOUR_MESSAGE with your text.\n" +
        "```powershell\n" +
        "powershell -NoProfile -Command \"$b = @{sender_id='{agent_id}'; recipient_id='{lead_id}'; text='YOUR_MESSAGE'} | ConvertTo-Json; " +
        "Invoke-RestMethod -Uri 'http://127.0.0.1:{port}/messages/send' -Method POST " +
        "-ContentType 'application/json; charset=utf-8' -Body ([System.Text.Encoding]::UTF8.GetBytes($b))\"\n" +
        "```\n\n" +
        "### Check your messages\n" +
        "```powershell\n" +
        "powershell -NoProfile -Command \"Invoke-RestMethod -Uri 'http://127.0.0.1:{port}/messages/{agent_id}'\"\n" +
        "```\n\n" +
        "### Discover other agents\n" +
        "`curl -s http://127.0.0.1:{port}/agents` — returns `[{\"id\",\"name\",\"agent_type\",\"task\"}]`. Use `id` as `recipient_id`.\n\n" +
        "### Completion report (MANDATORY)\n" +
        "Before finishing (AGENT_COMPLETED), send a brief report to `{lead_id}` (what was done + problems, under 500 chars). No exceptions.\n\n" +
        "### Boundaries\n" +
        "You CAN: send/check messages, discover agents. You CANNOT: use MCP tools (`mcp__ShikigamiMCP__*`), read other agents' messages, register/unregister agents.\n" +
        "PREFER `USER_INPUT_REQUIRED` over messaging for blocking questions — the runner handles re-launch with the answer automatically.\n";

    private const string DefaultPoolMcpHeader =
        "\n## MCP Connection\n" +
        "- **Port**: {port}\n" +
        "- **Your Agent ID**: {agent_id}\n" +
        "- **Your Lead ID**: {lead_id}\n" +
        "- **Pool ID**: {pool_id}\n" +
        "- **Send message**: POST http://127.0.0.1:{port}/pools/{pool_id}/messages/send\n" +
        "  Body: {\"sender_id\":\"{agent_id}\",\"recipient_id\":\"<target>\",\"text\":\"<message>\"}\n" +
        "- **Check messages**: GET http://127.0.0.1:{port}/pools/{pool_id}/messages/check?agent_id={agent_id}\n";
}
