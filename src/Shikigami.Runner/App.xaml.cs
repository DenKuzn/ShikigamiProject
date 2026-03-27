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

        return new AppArgs
        {
            Agent = Get("--agent"),
            Model = Get("--model"),
            Prompt = Get("--prompt"),
            PromptId = Get("--prompt-id"),
            Tools = Get("--tools"),
            Workdir = Get("--workdir"),
            McpPort = int.TryParse(Get("--mcp-port"), out var p) ? p : null,
            TaskMode = Has("--task-mode"),
            AgentType = Get("--agent-type"),
            PoolId = Get("--pool-id"),
            AgentId = Get("--agent-id"),
            Effort = Get("--effort"),
            LeadId = Get("--lead-id") ?? "lead",
        };
    }
}
