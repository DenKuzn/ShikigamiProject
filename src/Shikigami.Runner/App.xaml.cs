using System.IO;
using System.Windows;

namespace Shikigami.Runner;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var args = AppArgs.Parse(e.Args);
        var window = new MainWindow(args);
        window.Show();
    }
}

/// <summary>
/// Parsed command-line arguments for the Runner.
/// </summary>
public sealed class AppArgs
{
    public string? Agent { get; init; }
    public string? Model { get; init; }
    public string? Prompt { get; init; }
    public string? PromptId { get; init; }
    public string? Tools { get; init; }
    public string? Workdir { get; init; }
    public int? McpPort { get; init; }
    public bool TaskMode { get; init; }
    public string? AgentType { get; init; }
    public string? PoolId { get; init; }
    public string? AgentId { get; init; }
    public string? Effort { get; init; }
    public string LeadId { get; init; } = "lead";

    public static AppArgs Parse(string[] args)
    {
        string? Get(string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
        bool Has(string name) => args.Contains(name);

        var agent = Get("--agent");
        var workdir = Get("--workdir");
        var effort = Get("--effort");
        if (string.IsNullOrEmpty(effort) && !string.IsNullOrEmpty(agent))
            effort = TryResolveEffortFromAgentFile(agent, workdir);

        return new AppArgs
        {
            Agent = agent,
            Model = Get("--model"),
            Prompt = Get("--prompt"),
            PromptId = Get("--prompt-id"),
            Tools = Get("--tools"),
            Workdir = workdir,
            McpPort = int.TryParse(Get("--mcp-port"), out var p) ? p : null,
            TaskMode = Has("--task-mode"),
            AgentType = Get("--agent-type"),
            PoolId = Get("--pool-id"),
            AgentId = Get("--agent-id"),
            Effort = effort,
            LeadId = Get("--lead-id") ?? "lead",
        };
    }

    /// <summary>
    /// If the agent's .md file contains an "effort: <value>" key in its YAML
    /// frontmatter, return the value so it can be forwarded to the CLI as --effort.
    /// Looks in the project-local agents dir first, then the user-global one.
    /// </summary>
    private static string? TryResolveEffortFromAgentFile(string agentName, string? workdir)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(workdir))
            candidates.Add(Path.Combine(workdir, ".claude", "agents", $"{agentName}.md"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            candidates.Add(Path.Combine(home, ".claude", "agents", $"{agentName}.md"));

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;

            string content;
            try { content = File.ReadAllText(path); }
            catch { continue; }

            var frontmatter = ExtractFrontmatter(content);
            if (frontmatter == null) continue;

            foreach (var raw in frontmatter.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("effort:", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line.Substring("effort:".Length).Trim();
                if (value.Length >= 2 && (value[0] == '"' || value[0] == '\''))
                    value = value.Trim(value[0]);
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
        }
        return null;
    }

    private static string? ExtractFrontmatter(string content)
    {
        // Frontmatter block: leading "---" line, followed by keys, terminated by another "---" line.
        if (!content.StartsWith("---")) return null;
        var afterOpen = content.IndexOf('\n', 3);
        if (afterOpen < 0) return null;
        var close = content.IndexOf("\n---", afterOpen, StringComparison.Ordinal);
        if (close < 0) return null;
        return content.Substring(afterOpen + 1, close - afterOpen - 1);
    }
}
