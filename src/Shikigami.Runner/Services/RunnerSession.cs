using System.Diagnostics;
using System.Text.Json;

namespace Shikigami.Runner.Services;

/// <summary>
/// Orchestrates a single shikigami's lifecycle: launch CLI, evaluate results,
/// manage state transitions, handle messages.
///
/// Secret: the state machine (RunnerState), mode-specific logic (prompt vs horde),
/// marker evaluation, retry strategy, prompt assembly.
///
/// Exposes: commands that the UI can trigger (user input, stop, keep-active toggle).
/// </summary>
public sealed class RunnerSession
{
    /// <summary>
    /// Explicit state machine replacing 7 implicit booleans.
    /// Every state is mutually exclusive — no invalid combinations possible.
    /// </summary>
    private enum RunnerState
    {
        Starting,
        Working,
        WaitingInputQuestion,
        WaitingInputStop,
        Idle,
        HordeWaiting,
        Completing,
        Completed,
        Aborted,
    }

    private enum HordeOutcome { Completed, Failed, Error, NoMarker, UserStopped }

    // ── Dependencies ──
    private readonly AppArgs _args;
    private readonly IRunnerView _view;
    private readonly McpHttpClient _mcp;
    private readonly CliRunner _cli;
    private readonly SynchronizationContext _syncContext;
    private readonly ShikigamiContextMemory _memory = new();
    private readonly string _agentId;

    // ── Core state ──
    private PromptBuilder? _promptBuilder;
    private string? _originalPrompt;
    private RunnerState _state = RunnerState.Starting;
    private int _iteration;
    private int _toolCount;
    private double _totalCost;
    private int _tasksCompleted;
    private bool _userStopped;
    private bool _keepActive;

    // ── Horde-specific ──
    private string? _currentTaskId;
    private string? _currentTaskPrompt;
    private int _markerRetries;

    /// <summary>
    /// Dot color hint for the view's pulse animation.
    /// Derived from state — does not expose the state itself.
    /// </summary>
    public StatusColor DotColor => _state switch
    {
        RunnerState.WaitingInputQuestion or RunnerState.WaitingInputStop => StatusColor.Amber,
        RunnerState.Idle or RunnerState.HordeWaiting => StatusColor.Green,
        _ => StatusColor.Teal,
    };

    public RunnerSession(AppArgs args, IRunnerView view)
    {
        _args = args;
        _view = view;
        _mcp = new McpHttpClient(args.McpPort);
        _cli = new CliRunner(args.Agent, args.Model, args.Tools, args.Workdir, args.Effort);
        _syncContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("RunnerSession must be created on the UI thread");
        _agentId = args.AgentId ?? args.PromptId ?? "";

        if (args.TaskMode)
            _view.ShowTasksPanel();
    }

    // ════════════════════════════════════════════════════════════════
    //  Public API — called by MainWindow
    // ════════════════════════════════════════════════════════════════

    public async Task StartAsync()
    {
        await _mcp.ValidatePortAsync();

        if (_args.TaskMode)
        {
            await _mcp.PoolRegisterAsync(_args.PoolId!, _agentId, _args.AgentType!);
            _view.AppendLog("[shikigami] Registered in pool, requesting tasks...", "sys");
            await DispatchNextTaskAsync();
        }
        else
        {
            if (!string.IsNullOrEmpty(_args.PromptId) && _mcp.Active)
            {
                var resp = await _mcp.RequestAsync("GET", $"/prompts/{_args.PromptId}");
                if (resp?.TryGetProperty("text", out var tp) == true)
                    _originalPrompt = tp.GetString();
            }
            _originalPrompt ??= _args.Prompt ?? "No prompt provided.";

            _promptBuilder = new PromptBuilder(
                _originalPrompt,
                mcpPort: _mcp.Port,
                promptId: _args.PromptId,
                leadId: _args.LeadId);

            await _mcp.RegisterAsync(
                _args.PromptId ?? "",
                _args.Agent ?? _args.Model ?? "unknown",
                _originalPrompt,
                Process.GetCurrentProcess().Id,
                _args.LeadId);

            _view.AppendLog("[prompt] " + _promptBuilder.FullPromptDisplay(), "prompt");
            await LaunchPassAsync();
        }
    }

    public async Task OnUserInput(string text)
    {
        _view.ClearInput();
        _view.DisableInput();

        var isStop = _state == RunnerState.WaitingInputStop;

        var sep = new string('\u2500', 54);
        _view.AppendLog(sep, "sys");
        _view.AppendLog(isStop ? "  YOUR CORRECTION:" : "  YOUR ANSWER:", "result");
        _view.AppendLog($"  {text}", "result");
        _view.AppendLog(sep, "sys");

        if (isStop)
            _memory.AddUserStop(text);
        else
            _memory.AddUserInput(text);

        if (_args.TaskMode)
            await RelaunchHordeTaskAsync(isStop
                ? $"\n\nUser stopped you and instructed:\n{text}\n\nApply the correction and complete the task."
                : $"\n\nUser answered:\n{text}\n\nContinue the task.");
        else
            await LaunchPassAsync();
    }

    public void OnStopClicked()
    {
        if (_state != RunnerState.Working) return;
        _userStopped = true;
        _view.SetStopButton(false, 0.5, "\u505c\u6b62\u4e2d...");
        _cli.Kill();
    }

    public void OnKeepActiveToggled()
    {
        _keepActive = !_keepActive;
        _view.SetKeepActiveVisual(_keepActive);

        if (_keepActive && _state == RunnerState.Completing)
        {
            _view.CancelCloseCountdown();
            EnterIdle();
        }
    }

    public async Task PollMessagesAsync()
    {
        if (!_mcp.Active || _state == RunnerState.Completing) return;
        try
        {
            var messages = _args.TaskMode
                ? await _mcp.PoolCheckMessagesAsync(_args.PoolId!, _agentId)
                : await _mcp.CheckMessagesAsync(_args.PromptId ?? "");

            if (messages.Count == 0) return;

            var parts = new List<string>();
            foreach (var msg in messages)
            {
                var sender = msg.TryGetProperty("sender_id", out var s) ? s.GetString() : "?";
                var text = msg.TryGetProperty("text", out var t) ? t.GetString() : "";
                parts.Add($"[Message from {sender}]: {text}");
            }
            var combined = string.Join("\n", parts);

            var sep = new string('\u2500', 54);
            _view.AppendLog(sep, "sys");
            _view.AppendLog("  \u2709 MESSAGE RECEIVED:", "task");
            _view.AppendLog($"  {combined}", "text");
            _view.AppendLog(sep, "sys");

            if (_state != RunnerState.Working)
            {
                _memory.AddMessage(combined);

                if (_state is RunnerState.WaitingInputQuestion or RunnerState.WaitingInputStop)
                {
                    _view.DisableInput();
                    _view.ClearInput();
                }
                if (_state == RunnerState.Idle) ExitIdle();

                if (_args.TaskMode && _state == RunnerState.HordeWaiting)
                {
                    // In horde waiting state, message doesn't re-launch — just display
                }
                else if (_args.TaskMode)
                {
                    await RelaunchHordeTaskAsync($"\n\nMessage received:\n{combined}\n\nContinue the task.");
                }
                else
                {
                    await LaunchPassAsync();
                }
            }
        }
        catch { /* polling failure is not critical */ }
    }

    public async Task OnHordePollTick()
    {
        await DispatchNextTaskAsync();
    }

    public void Shutdown()
    {
        _view.StopHordePoll();
        _cli.Kill();
        if (_args.TaskMode)
            _ = _mcp.PoolUnregisterAsync(_args.PoolId!, _agentId);
        else
            _ = _mcp.UnregisterAsync();
    }

    // ════════════════════════════════════════════════════════════════
    //  CLI pass helpers
    // ════════════════════════════════════════════════════════════════

    private void BeginCliPass()
    {
        _state = RunnerState.Working;
        _userStopped = false;
        _view.SetHeaderStatus("working", StatusColor.Teal);
        _view.SetStopButton(true, 1.0);
    }

    private void FinishCliPass(RunResult result)
    {
        _memory.FlushEvents(result.Events, _iteration);

        if (result.Cost.HasValue)
        {
            _totalCost += result.Cost.Value;
            _view.SetStat(StatField.Cost, $"${_totalCost:F4}");
            _ = _mcp.SubmitCostAsync(_totalCost);
        }

        _view.SetStopButton(false, 0.25);
    }

    private async Task<RunResult> RunCliPassAsync(string prompt)
    {
        BeginCliPass();
        var result = await Task.Run(() => _cli.Run(prompt, (type, data) =>
        {
            _syncContext.Send(_ => HandleCliEvent(type, data), null);
        }));
        FinishCliPass(result);
        return result;
    }

    private void HandleCliEvent(string type, Dictionary<string, object> data)
    {
        switch (type)
        {
            case "system":
                _view.AppendLog($"[system] model={data["model"]}", "sys");
                break;
            case "tool":
                _toolCount++;
                _view.SetStat(StatField.Tools, _toolCount.ToString());
                var name = data["name"];
                var detail = data.TryGetValue("detail", out var d) ? d : "";
                _view.AppendLog($"  [{_toolCount}] {name}  {detail}", "tool");
                break;
            case "text":
                _view.AppendLog($"  {data["text"]}", "text");
                break;
            case "thinking":
                _view.AppendLog("  (thinking...)", "dim");
                break;
            case "error":
                _view.AppendLog($"  ERROR: {data["message"]}", "error");
                break;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Prompt mode
    // ════════════════════════════════════════════════════════════════

    private async Task LaunchPassAsync()
    {
        if (_promptBuilder == null) return;
        if (_state == RunnerState.Idle) ExitIdle();

        _iteration++;
        _view.SetStat(StatField.Iteration, _iteration.ToString());
        await _mcp.UpdateStateAsync("working", $"Iteration {_iteration}");

        var builtPrompt = _promptBuilder.Build(_iteration, _iteration > 1 ? _memory.ToJson() : null);
        var result = await RunCliPassAsync(builtPrompt);

        if (_userStopped)
        {
            _userStopped = false;
            AskUserAfterStop();
            return;
        }

        if (result.ResultText.Contains("USER_INPUT_REQUIRED"))
        {
            var marker = "USER_INPUT_REQUIRED:";
            var idx = result.ResultText.LastIndexOf(marker);
            var question = idx >= 0
                ? result.ResultText[(idx + marker.Length)..].Trim()
                : "";
            AskUser(question);
            return;
        }

        if (result.ResultText.Contains("AGENT_IDLE"))
        {
            _view.AppendLog($"[done] Result: {Truncate(result.ResultText, 300)}", "result");
            _ = _mcp.SubmitLogAsync(result.Events, result.ResultText);
            EnterIdle();
            return;
        }

        if (result.ResultText.Contains("AGENT_COMPLETED"))
        {
            _view.AppendLog($"[done] Result: {Truncate(result.ResultText, 300)}", "result");
            _ = _mcp.SubmitLogAsync(result.Events, result.ResultText);
            CompleteWithCountdown();
            return;
        }

        _view.AppendLog("[warn] No completion marker — re-launching for correction...", "error");
        await LaunchPassAsync();
    }

    // ════════════════════════════════════════════════════════════════
    //  Horde mode
    // ════════════════════════════════════════════════════════════════

    private async Task<HordeOutcome> EvaluateHordeResult(RunResult result)
    {
        if (_userStopped)
        {
            _userStopped = false;
            AskUserAfterStop();
            return HordeOutcome.UserStopped;
        }

        if (result.Error != null)
        {
            _view.AppendLog($"[horde] Task failed (CLI error): {result.Error}", "error");
            await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, _agentId, result.Error);
            return HordeOutcome.Error;
        }

        if (result.ResultText.Contains("USER_INPUT_REQUIRED"))
        {
            var marker = "USER_INPUT_REQUIRED:";
            var idx = result.ResultText.LastIndexOf(marker);
            var question = idx >= 0
                ? result.ResultText[(idx + marker.Length)..].Trim()
                : "";
            AskUser(question);
            return HordeOutcome.UserStopped;
        }

        if (result.ResultText.Contains("TASK_FAILED"))
        {
            var marker = "TASK_FAILED:";
            var idx = result.ResultText.LastIndexOf(marker);
            var reason = idx >= 0
                ? result.ResultText[(idx + marker.Length)..].Trim()
                : "Agent reported failure";
            _view.AppendLog($"[horde] Task failed: {reason}", "error");
            await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, _agentId, reason);
            return HordeOutcome.Failed;
        }

        if (result.ResultText.Contains("TASK_COMPLETED"))
        {
            _tasksCompleted++;
            _view.SetStat(StatField.Tasks, _tasksCompleted.ToString());
            _view.AppendLog($"[horde] Task completed: {Truncate(result.ResultText, 200)}", "result");
            await _mcp.CompleteTaskAsync(_args.PoolId!, _currentTaskId!, _agentId, result.ResultText);
            return HordeOutcome.Completed;
        }

        return HordeOutcome.NoMarker;
    }

    private string BuildHordePromptWithHistory(string? suffix = null)
    {
        var parts = new List<string> { _currentTaskPrompt! };
        var historyJson = _memory.CurrentTaskJson();
        if (!string.IsNullOrEmpty(historyJson))
        {
            parts.Add($"## Full History (current task)\n```json\n{historyJson}\n```");
            parts.Add("Continue from where you left off. " +
                       "Do NOT re-read files you already have in history.");
        }
        if (!string.IsNullOrEmpty(suffix))
            parts.Add(suffix.TrimStart('\n'));
        return string.Join("\n\n", parts);
    }

    private async Task DispatchNextTaskAsync()
    {
        _view.StopHordePoll();

        var resp = await _mcp.RequestTaskAsync(_args.PoolId!, _args.AgentType!, _agentId);

        // Pool aborted
        if (resp?.TryGetProperty("error", out var errProp) == true
            && errProp.GetString() == "Pool aborted")
        {
            _view.AppendLog($"[horde] Pool aborted ({_tasksCompleted} done).", "error");
            await _mcp.PoolUnregisterAsync(_args.PoolId!, _agentId);
            _state = RunnerState.Aborted;
            _view.SetHeaderStatus("aborted", StatusColor.Red);
            return;
        }

        // Got a task
        if (resp?.TryGetProperty("task", out var taskProp) == true
            && taskProp.ValueKind != JsonValueKind.Null)
        {
            var task = taskProp;
            _currentTaskId = task.GetProperty("id").GetString();
            var title = task.GetProperty("title").GetString();
            var description = task.GetProperty("description").GetString();

            _memory.BeginTask(_currentTaskId!);

            _view.AppendLog($"[horde] Task: {title}", "task");
            _iteration++;
            _view.SetStat(StatField.Iteration, _iteration.ToString());
            await _mcp.PoolUpdateStateAsync(_args.PoolId!, _agentId, "working", $"Task: {title}");

            _currentTaskPrompt = PromptBuilder.BuildTaskPrompt(
                title!, description!, _mcp.Port!.Value, _agentId, _args.PoolId!, _args.LeadId);

            var result = await RunCliPassAsync(_currentTaskPrompt);
            var outcome = await EvaluateHordeResult(result);

            if (outcome == HordeOutcome.UserStopped) return;

            if (outcome == HordeOutcome.NoMarker && _markerRetries == 0)
            {
                _markerRetries++;
                _view.AppendLog("[horde] No completion marker — re-launching for correction...", "error");
                _iteration++;
                _view.SetStat(StatField.Iteration, _iteration.ToString());

                var correctionPrompt = BuildHordePromptWithHistory(
                    "You did NOT include a completion marker in your previous response. " +
                    "You MUST end with one of:\n- TASK_COMPLETED\n- TASK_FAILED: <reason>");

                var retryResult = await RunCliPassAsync(correctionPrompt);
                var retryOutcome = await EvaluateHordeResult(retryResult);

                if (retryOutcome == HordeOutcome.UserStopped) { _markerRetries = 0; return; }
                if (retryOutcome == HordeOutcome.NoMarker)
                {
                    _view.AppendLog("[horde] Still no marker after correction — failing task", "error");
                    await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, _agentId,
                        "No completion marker after correction attempt");
                }
            }

            _markerRetries = 0;
            await DispatchNextTaskAsync();
            return;
        }

        // All done
        if (resp?.TryGetProperty("all_done", out var ad) == true && ad.GetBoolean())
        {
            _view.AppendLog($"[horde] All tasks done ({_tasksCompleted} completed).", "sys");
            await _mcp.PoolUnregisterAsync(_args.PoolId!, _agentId);
            CompleteWithCountdown();
            return;
        }

        // Blocked — check if our agent_type has pending tasks
        var blockedTypes = new List<string>();
        if (resp?.TryGetProperty("blocked_agent_types", out var btProp) == true
            && btProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var bt in btProp.EnumerateArray())
                blockedTypes.Add(bt.GetString() ?? "");
        }

        if (blockedTypes.Contains(_args.AgentType!))
        {
            var blockedCount = resp?.TryGetProperty("blocked_count", out var bc) == true ? bc.GetInt32() : 0;
            _view.AppendLog($"[horde] Waiting ({blockedCount} blocked)...", "sys");
            _state = RunnerState.HordeWaiting;
            _view.SetHeaderStatus($"waiting ({blockedCount} blocked)", StatusColor.Amber);
            await _mcp.PoolUpdateStateAsync(_args.PoolId!, _agentId, "idle", "Waiting for tasks");
            _view.ScheduleHordePoll();
        }
        else
        {
            _view.AppendLog($"[horde] No more tasks for {_args.AgentType} ({_tasksCompleted} done).", "sys");
            await _mcp.PoolUnregisterAsync(_args.PoolId!, _agentId);
            CompleteWithCountdown();
        }
    }

    private async Task RelaunchHordeTaskAsync(string suffix)
    {
        if (_currentTaskPrompt == null) return;

        _iteration++;
        _view.SetStat(StatField.Iteration, _iteration.ToString());
        await _mcp.PoolUpdateStateAsync(_args.PoolId!, _agentId, "working", "Continuing task");

        var prompt = BuildHordePromptWithHistory(suffix);
        var result = await RunCliPassAsync(prompt);
        var outcome = await EvaluateHordeResult(result);

        if (outcome == HordeOutcome.UserStopped) return;

        if (outcome == HordeOutcome.NoMarker)
        {
            _view.AppendLog("[horde] No completion marker — failing task", "error");
            await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, _agentId,
                "No completion marker after user correction");
        }

        await DispatchNextTaskAsync();
    }

    // ════════════════════════════════════════════════════════════════
    //  State transitions
    // ════════════════════════════════════════════════════════════════

    private void EnterIdle()
    {
        _state = RunnerState.Idle;

        var sep = new string('\u2500', 54);
        _view.AppendLog(sep, "sys");
        _view.AppendLog("  \u23f3 AGENT IDLE \u2014 waiting for messages or new instructions", "result");
        _view.AppendLog("  Type below or send a message via MCP.", "sys");
        _view.AppendLog(sep, "sys");

        _view.SetHeaderStatus("idle", StatusColor.Green);
        _ = _mcp.UpdateStateAsync("idle", "Waiting for messages");

        _view.EnableInput();
        _view.FocusInput();
    }

    private void ExitIdle()
    {
        _view.DisableInput();
        _view.ClearInput();
    }

    private void CompleteWithCountdown()
    {
        if (_keepActive)
        {
            EnterIdle();
            return;
        }

        _state = RunnerState.Completing;
        _ = _mcp.UpdateStateAsync("completed");
        _view.SetHeaderStatus("completed \u2014 closing in 10s", StatusColor.Green);
        _view.StartCloseCountdown(10);
    }

    private void AskUserAfterStop()
    {
        _state = RunnerState.WaitingInputStop;
        _view.SetStopButton(false, 0.25, "\u25a0 \u505c\u6b62");

        var sep = new string('\u2500', 54);
        _view.AppendLog(sep, "sys");
        _view.AppendLog("  \u26a0 STOPPED BY USER", "error");
        _view.AppendLog("  Enter correction or instructions below:", "task");
        _view.AppendLog(sep, "sys");

        _view.SetHeaderStatus("stopped \u2014 awaiting input", StatusColor.Amber);
        _ = _mcp.UpdateStateAsync("waiting", "Stopped by user, awaiting correction");

        _view.EnableInput();
        _view.FocusInput();
    }

    private void AskUser(string question)
    {
        _state = RunnerState.WaitingInputQuestion;
        var sep = new string('\u2500', 54);
        _view.AppendLog(sep, "sys");
        _view.AppendLog("  AGENT ASKS:", "task");
        if (!string.IsNullOrEmpty(question))
            _view.AppendLog($"  {question}", "task");
        _view.AppendLog(sep, "sys");

        _view.SetHeaderStatus("awaiting input", StatusColor.Amber);
        _ = _mcp.UpdateStateAsync("waiting", $"USER_INPUT_REQUIRED: {Truncate(question, 100)}");

        _view.EnableInput();
        _view.FocusInput();
    }

    // ════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
