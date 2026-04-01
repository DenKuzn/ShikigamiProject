using System.IO;

namespace Shikigami.Runner.Services;

/// <summary>
/// Builds the initial prompt for a shikigami's CLI session.
/// Includes MCP header, communication directives, and the task.
///
/// With persistent CLI sessions, only the FIRST message needs the full prompt.
/// Subsequent messages are raw text — the CLI maintains context internally.
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

        var baseDir = Path.Combine(AppContext.BaseDirectory, "Prompts");
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
    /// Build the initial prompt (first message in a persistent CLI session).
    /// Includes MCP header + communication directive + task.
    /// Also used for display in the Runner log.
    /// </summary>
    public string BuildInitialPrompt()
    {
        var suffix = _skipCommDirective ? "" : _commDirective;
        return McpHeader() + suffix + $"\n## Your task:\n{_originalPrompt}";
    }

    /// <summary>
    /// Build the full prompt for a Horde task.
    /// </summary>
    public static string BuildTaskPrompt(
        string title, string description,
        int mcpPort, string agentId, string poolId, string leadId = "lead")
    {
        var baseDir = Path.Combine(AppContext.BaseDirectory, "Prompts");
        var hordeTemplate = LoadTemplate(baseDir, "prompt_horde_comm.txt", DefaultHordeCommDirective);
        var poolTemplate = LoadTemplate(baseDir, "prompt_pool_mcp_header.txt", DefaultPoolMcpHeader);

        var commDirective = hordeTemplate.Replace("{title}", title);
        var mcpHeader = poolTemplate
            .Replace("{port}", mcpPort.ToString())
            .Replace("{agent_id}", agentId)
            .Replace("{lead_id}", leadId)
            .Replace("{pool_id}", poolId);

        return $"{mcpHeader}\n{commDirective}\n\n## Task: {title}\n\n{description}";
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
        "A response without a marker is treated as an error.\n\n" +
        "## Result Format\n" +
        "When finishing (AGENT_COMPLETED or AGENT_IDLE), wrap your final result summary in markers:\n\n" +
        "AGENT_RESULT_BEGIN\n" +
        "<your result summary here>\n" +
        "AGENT_RESULT_END\n\n" +
        "AGENT_COMPLETED\n\n" +
        "The text between AGENT_RESULT_BEGIN and AGENT_RESULT_END is captured as your result. " +
        "Without these markers, the result may be lost or incomplete. " +
        "Place them in the same response as your completion marker.\n";

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
        "## Result Format\n" +
        "When finishing (TASK_COMPLETED), wrap your result summary in markers:\n\n" +
        "AGENT_RESULT_BEGIN\n" +
        "Task \"{title}\" completed. <brief summary of what was done, deviations, additions>\n" +
        "AGENT_RESULT_END\n\n" +
        "TASK_COMPLETED\n\n" +
        "The text between AGENT_RESULT_BEGIN and AGENT_RESULT_END is captured as your result. " +
        "Without these markers, the result may be lost or incomplete. " +
        "Place them in the same response as your completion marker.\n";

    private const string DefaultMcpHeader =
        "## MCP Connection\n" +
        "- **Port**: {port}\n" +
        "- **Your Agent ID**: {agent_id}\n" +
        "- **Your Lead ID**: {lead_id}\n\n" +
        "### Send message\n" +
        "CRITICAL: Use exactly this pattern — printf piped to curl. Direct curl -d breaks Cyrillic on Windows.\n" +
        "Set `recipient_id` to `{lead_id}` (your lead) or another agent's ID. Replace YOUR_MESSAGE with your text.\n" +
        "```bash\n" +
        "printf '{\"sender_id\":\"{agent_id}\",\"recipient_id\":\"{lead_id}\",\"text\":\"YOUR_MESSAGE\"}' " +
        "| curl -s -X POST http://127.0.0.1:{port}/messages/send " +
        "-H \"Content-Type: application/json; charset=utf-8\" -d @-\n" +
        "```\n\n" +
        "### Check your messages\n" +
        "```bash\n" +
        "curl -s \"http://127.0.0.1:{port}/messages/{agent_id}\"\n" +
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
