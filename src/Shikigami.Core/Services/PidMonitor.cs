using System.Diagnostics;
using Shikigami.Core.State;

namespace Shikigami.Core.Services;

/// <summary>
/// Periodically checks if registered shikigami processes are still alive.
/// Marks dead agents and cleans up their state.
/// </summary>
public sealed class PidMonitor
{
    private readonly ShikigamiState _state;
    private readonly TimeSpan _interval;

    public PidMonitor(ShikigamiState state, TimeSpan? interval = null)
    {
        _state = state;
        _interval = interval ?? TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Run the PID monitor loop. Call this as a background task.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_interval, ct);

            // Check prompt-mode agents
            foreach (var (agentId, info) in _state.Agents)
            {
                if (info.Active && !IsPidAlive(info.Pid))
                {
                    _state.MarkDead(agentId);
                    Console.Error.WriteLine(
                        $"[pid-monitor] Shikigami {agentId} ({info.Name}) pid={info.Pid} dead");
                }
            }

            // Check Horde pool agents
            foreach (var (poolId, pool) in _state.Pools)
            {
                if (pool.Status != "in_progress") continue;

                foreach (var (agentId, agentInfo) in pool.Agents)
                {
                    if (agentInfo.Active && !IsPidAlive(agentInfo.Pid))
                    {
                        _state.MarkDeadPoolAgent(poolId, agentId);
                        Console.Error.WriteLine(
                            $"[pid-monitor] Horde shikigami {agentId} (pool={poolId}, type={agentInfo.AgentType}) dead");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Check if a process with the given PID is still running.
    /// </summary>
    public static bool IsPidAlive(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
