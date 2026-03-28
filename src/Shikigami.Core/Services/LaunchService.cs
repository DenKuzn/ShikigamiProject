using System.Diagnostics;
using Shikigami.Core.Models;
using Shikigami.Core.State;

namespace Shikigami.Core.Services;

/// <summary>
/// Launches Shikigami.Runner processes for prompt-mode and horde-mode agents.
/// </summary>
public sealed class LaunchService
{
    private readonly ShikigamiState _state;
    private readonly IdGenerator _idGen;
    private readonly PoolService _poolService;

    public LaunchService(ShikigamiState state, IdGenerator idGen, PoolService poolService)
    {
        _state = state;
        _idGen = idGen;
        _poolService = poolService;
    }

    /// <summary>
    /// Launch a single prompt-mode shikigami.
    /// </summary>
    public Dictionary<string, object> LaunchPromptAgent(
        string prompt,
        string agentName = "",
        string model = "",
        string tools = "",
        string workdir = "",
        string leadId = "lead")
    {
        if (string.IsNullOrEmpty(agentName) && string.IsNullOrEmpty(model))
            return Error("Either agent_name or model must be specified");
        if (!string.IsNullOrEmpty(agentName) && !string.IsNullOrEmpty(model))
            return Error("Specify agent_name OR model, not both");

        var (resolvedWorkdir, err) = ResolveWorkdir(workdir);
        if (err != null) return Error(err);

        var agentId = _idGen.NewAgentId();

        _state.Prompts[agentId] = new PromptRecord
        {
            Id = agentId,
            Text = prompt,
        };

        var runnerPath = FindRunnerExecutable();
        if (runnerPath == null)
            return Error("Shikigami.Runner executable not found");

        var args = $"--prompt-id {agentId} --mcp-port {_state.HttpPort} --workdir \"{resolvedWorkdir}\" --lead-id {leadId}";
        if (!string.IsNullOrEmpty(agentName))
            args += $" --agent {agentName}";
        else
            args += $" --model {model}";
        if (!string.IsNullOrEmpty(tools))
            args += $" --tools {tools}";

        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = runnerPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (proc == null) return Error("Failed to start Runner process");

            return new Dictionary<string, object>
            {
                ["agent_id"] = agentId,
                ["pid"] = proc.Id,
                ["port"] = _state.HttpPort,
            };
        }
        catch (Exception e)
        {
            return Error($"Failed to launch: {e.Message}");
        }
    }

    /// <summary>
    /// Create a pool of tasks and auto-launch horde agents.
    /// </summary>
    public Dictionary<string, object> LaunchPool(
        List<Dictionary<string, object>> tasksBatch,
        string poolName = "",
        string workdir = "",
        string leadId = "lead")
    {
        var validationError = _poolService.ValidateTasks(tasksBatch);
        if (validationError != null) return Error(validationError);

        var (resolvedWorkdir, err) = ResolveWorkdir(workdir);
        if (err != null) return Error(err);

        var poolId = _idGen.NewPoolId();
        var pool = _poolService.CreatePool(poolId, tasksBatch, poolName);

        var uniqueTypes = tasksBatch
            .Select(t => t["agent_type"].ToString()!)
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        var launched = new List<Dictionary<string, object>>();
        foreach (var agentType in uniqueTypes)
        {
            var agentId = _idGen.NewAgentId();
            var pid = LaunchTaskAgent(agentId, agentType, poolId, resolvedWorkdir!, leadId);
            if (pid == null) continue;

            pool.Agents[agentId] = new PoolAgentInfo
            {
                AgentType = agentType,
                Pid = pid.Value,
            };
            pool.Queues[agentId] = new MessageQueue();
            launched.Add(new Dictionary<string, object>
            {
                ["agent_type"] = agentType,
                ["agent_id"] = agentId,
            });
        }

        return new Dictionary<string, object>
        {
            ["ok"] = true,
            ["pool_id"] = poolId,
            ["tasks_count"] = tasksBatch.Count,
            ["agents_launched"] = launched,
        };
    }

    private int? LaunchTaskAgent(string agentId, string agentType, string poolId, string workdir, string leadId)
    {
        var runnerPath = FindRunnerExecutable();
        if (runnerPath == null) return null;

        var args = $"--task-mode --agent-type {agentType} --agent {agentType} " +
                   $"--pool-id {poolId} --agent-id {agentId} " +
                   $"--mcp-port {_state.HttpPort} --workdir \"{workdir}\" --lead-id {leadId}";

        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = runnerPath,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return proc?.Id;
        }
        catch
        {
            return null;
        }
    }

    private (string? workdir, string? error) ResolveWorkdir(string workdir)
    {
        if (!string.IsNullOrEmpty(workdir))
        {
            _state.DefaultWorkdir = workdir;
            return (workdir, null);
        }
        if (!string.IsNullOrEmpty(_state.DefaultWorkdir))
            return (_state.DefaultWorkdir, null);
        return (null, "workdir is required (no default set yet)");
    }

    private static string? FindRunnerExecutable()
    {
        // Layout: ~/.claude/MCPs/ShikigamiMCP/Server/ (us) and ~/…/Runner/
        var serverDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var mcpRoot = Path.GetDirectoryName(serverDir); // ShikigamiMCP/
        if (mcpRoot != null)
        {
            var runnerPath = Path.Combine(mcpRoot, "Runner", "Shikigami.Runner.exe");
            if (File.Exists(runnerPath)) return Path.GetFullPath(runnerPath);
        }

        return null;
    }

    private static Dictionary<string, object> Error(string message) =>
        new() { ["error"] = message };
}
