using System.Diagnostics;
using System.Text.Json;

namespace Shikigami.Runner.Services;

/// <summary>
/// Orchestrates a single shikigami's lifecycle using a persistent CLI session.
///
/// The CLI process stays alive between messages — context is maintained internally
/// by the Claude Code harness. No more prompt rebuilding or history injection.
///
/// Flow: Start() → CLI alive → SendMessage()* → Close()/Kill()
/// Crash recovery: Kill() → Restart(resume) → SendMessage("continue")
/// </summary>
public sealed class RunnerSession
{
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
    private readonly CliSession _cli;
    private readonly SynchronizationContext _syncContext;
    private readonly ShikigamiContextMemory _memory = new();
    private readonly string _agentId;

    // ── Core state ──
    private PromptBuilder? _promptBuilder;
    private string? _originalPrompt;
    private RunnerState _state = RunnerState.Starting;
    private int _turn;
    private int _toolCount;
    private double _totalCost;
    private int _tasksCompleted;
    private bool _userStopped;
    private bool _keepActive;
    private volatile bool _shuttingDown;

    // ── Prompt-specific ──
    private int _promptMarkerRetries;
    private const int MaxPromptMarkerRetries = 3;

    // ── Horde-specific ──
    private string? _currentTaskId;
    private bool _hordeInitialPromptSent;
    private int _markerRetries;

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
        _cli = new CliSession(args.Agent, args.Model, args.Tools, args.Workdir, args.Effort);
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

        // Launch the persistent CLI process
        _cli.Start();
        _view.AppendLog($"[session] CLI started (session={_cli.SessionId[..8]}...)", "sys");

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

            // Display prompt in log
            var fullPrompt = _promptBuilder.BuildInitialPrompt();
            var taskMarker = "\n## Your task:";
            var taskIdx = fullPrompt.IndexOf(taskMarker);
            if (taskIdx >= 0)
            {
                _view.AppendCollapsible("[Base Shikigami Prompt]", fullPrompt[..taskIdx].Trim(), "prompt", "prompt");
                _view.AppendCollapsible("[Shikigami Task]", fullPrompt[(taskIdx + 1)..].Trim(), "task", "task");
            }
            else
            {
                _view.AppendCollapsible("[Base Shikigami Prompt]", fullPrompt, "prompt", "prompt");
            }

            // Send initial prompt as first message
            var result = await SendMessageAsync(fullPrompt);
            await EvaluatePromptResult(result);
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

        if (isStop)
        {
            // After stop, CLI was killed — restart with resume to restore context
            _cli.Restart(resume: true);
            _view.AppendLog("[session] Resumed after stop", "sys");
        }

        if (_args.TaskMode)
        {
            var message = isStop
                ? $"User stopped you and instructed:\n{text}\n\nApply the correction and complete the task."
                : $"User answered:\n{text}\n\nContinue the task.";
            var result = await SendMessageAsync(message);
            var outcome = await EvaluateHordeResult(result);
            if (outcome == HordeOutcome.UserStopped) return;
            if (outcome == HordeOutcome.NoMarker)
            {
                _view.AppendLog("[horde] No completion marker — failing task", "error");
                await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, _agentId,
                    "No completion marker after user input");
            }
            await DispatchNextTaskAsync();
        }
        else
        {
            // Just send the text — CLI has full context
            var result = await SendMessageAsync(text);
            await EvaluatePromptResult(result);
        }
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
        if (!_mcp.Active) return;
        if (_state is not (RunnerState.Idle or RunnerState.WaitingInputQuestion
                       or RunnerState.WaitingInputStop or RunnerState.HordeWaiting)) return;

        try
        {
            var messages = _args.TaskMode
                ? await _mcp.PoolCheckMessagesAsync(_args.PoolId!, _agentId)
                : await _mcp.CheckMessagesAsync(_args.PromptId ?? "");

            if (messages.Count == 0) return;

            var parts = new List<string>();
            foreach (var msg in messages)
            {
                var sender = msg.TryGetProperty("senderId", out var s) ? s.GetString() : "?";
                var text = msg.TryGetProperty("text", out var t) ? t.GetString() : "";
                parts.Add($"[Message from {sender}]: {text}");
            }
            var combined = string.Join("\n", parts);

            var sep = new string('\u2500', 54);
            _view.AppendLog(sep, "sys");
            _view.AppendLog("  \u2709 MESSAGE RECEIVED:", "task");
            _view.AppendLog($"  {combined}", "text");
            _view.AppendLog(sep, "sys");

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
                var result = await SendMessageAsync($"Message received:\n{combined}\n\nContinue the task.");
                var outcome = await EvaluateHordeResult(result);
                if (outcome == HordeOutcome.UserStopped) return;
                if (outcome == HordeOutcome.NoMarker)
                {
                    _view.AppendLog("[horde] No completion marker — failing task", "error");
                    await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, _agentId,
                        "No completion marker after message");
                }
                await DispatchNextTaskAsync();
            }
            else
            {
                // Prompt mode — just send the message text in existing session
                EnsureCliAlive();
                var result = await SendMessageAsync(combined);
                await EvaluatePromptResult(result);
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
        _shuttingDown = true;
        _view.StopHordePoll();
        _cli.Close();

        try
        {
            var task = _args.TaskMode
                ? _mcp.PoolUnregisterAsync(_args.PoolId!, _agentId)
                : _mcp.UnregisterAsync();
            task.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════════
    //  CLI message helpers
    // ════════════════════════════════════════════════════════════════

    private void BeginCliTurn()
    {
        _state = RunnerState.Working;
        _userStopped = false;
        _view.SetHeaderStatus("working", StatusColor.Teal);
        _view.SetStopButton(true, 1.0);
        _ = _mcp.UpdateStateAsync("working", $"Turn {_turn}");
    }

    private void FinishCliTurn(RunResult result)
    {
        _memory.FlushEvents(result.Events, _turn);

        if (result.Cost.HasValue)
        {
            _totalCost = result.Cost.Value; // Persistent session: cost is cumulative from CLI
            _view.SetStat(StatField.Cost, $"${_totalCost:F4}");
            _ = _mcp.SubmitCostAsync(_totalCost);
        }

        if (result.InputTokens > 0)
        {
            if (result.ContextWindow > 0)
            {
                var pct = (int)(100.0 * result.InputTokens / result.ContextWindow);
                _view.SetStat(StatField.Context, $"{FormatTokens(result.InputTokens)} / {FormatTokens(result.ContextWindow)} ({pct}%)");
            }
            else
            {
                _view.SetStat(StatField.Context, FormatTokens(result.InputTokens));
            }
        }

        _view.SetStopButton(false, 0.25);
    }

    /// <summary>
    /// Send a message in the persistent CLI session. The process stays alive after.
    /// If the process died, attempts crash recovery with --resume.
    /// </summary>
    private async Task<RunResult> SendMessageAsync(string content)
    {
        EnsureCliAlive();

        _turn++;
        _view.SetStat(StatField.Iteration, _turn.ToString());

        BeginCliTurn();
        var result = await Task.Run(() => _cli.SendMessage(content, (type, data) =>
        {
            if (_shuttingDown) return;
            _syncContext.Send(_ => HandleCliEvent(type, data), null);
        }));

        if (_shuttingDown) return result;

        // Check if process died during this turn
        if (result.Error != null && !_userStopped && !_cli.IsAlive)
        {
            _view.AppendLog($"[session] CLI crashed: {result.Error}", "error");
        }

        FinishCliTurn(result);
        return result;
    }

    /// <summary>
    /// If CLI process died unexpectedly, restart with --resume.
    /// </summary>
    private void EnsureCliAlive()
    {
        if (_cli.IsAlive) return;
        var stderr = _cli.LastStderr;
        _view.AppendLog("[session] CLI not alive — restarting with resume...", "error");
        if (!string.IsNullOrEmpty(stderr))
            _view.AppendLog($"[stderr] {stderr}", "error");
        _cli.Restart(resume: true);
        _view.AppendLog("[session] Resumed", "sys");
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
                _view.AppendLog($"{data["text"]}", "text");
                break;
            case "thinking":
                var thinkText = data.TryGetValue("text", out var th) ? th?.ToString() ?? "" : "";
                if (!string.IsNullOrEmpty(thinkText))
                    _view.AppendCollapsible("  (thinking...)", thinkText, "dim", "dim");
                else
                    _view.AppendLog("  (thinking...)", "dim");
                break;
            case "usage":
                var inpTok = (int)data["input_tokens"];
                if (inpTok > 0)
                    _view.SetStat(StatField.Context, FormatTokens(inpTok));
                break;
            case "marked_result":
                var markedText = data["text"].ToString() ?? "";
                _ = _mcp.SubmitLogAsync(new List<Dictionary<string, object>>(), markedText);
                break;
            case "error":
                _view.AppendLog($"  ERROR: {data["message"]}", "error");
                break;
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  Prompt mode
    // ════════════════════════════════════════════════════════════════

    private async Task EvaluatePromptResult(RunResult result)
    {
        if (_userStopped)
        {
            _userStopped = false;
            AskUserAfterStop();
            return;
        }

        var markerText = GetMarkerText(result);

        if (markerText.Contains("USER_INPUT_REQUIRED"))
        {
            _promptMarkerRetries = 0;
            var marker = "USER_INPUT_REQUIRED:";
            var idx = markerText.LastIndexOf(marker);
            var question = idx >= 0
                ? markerText[(idx + marker.Length)..].Trim()
                : "";
            AskUser(question);
            return;
        }

        if (markerText.Contains("AGENT_IDLE"))
        {
            _promptMarkerRetries = 0;
            _view.AppendLog("[done]", "result");
            _ = _mcp.SubmitLogAsync(result.Events, result.MarkedResult ?? result.ResultText);
            EnterIdle();
            return;
        }

        if (markerText.Contains("AGENT_COMPLETED"))
        {
            _promptMarkerRetries = 0;
            _view.AppendLog("[done]", "result");
            _ = _mcp.SubmitLogAsync(result.Events, result.MarkedResult ?? result.ResultText);
            CompleteWithCountdown();
            return;
        }

        // No marker — send correction in same session (no relaunch needed!)
        _promptMarkerRetries++;
        if (_promptMarkerRetries >= MaxPromptMarkerRetries)
        {
            _view.AppendLog($"[error] No completion marker after {MaxPromptMarkerRetries} retries — completing.", "error");
            _promptMarkerRetries = 0;
            _ = _mcp.SubmitLogAsync(result.Events, result.MarkedResult ?? result.ResultText);
            CompleteWithCountdown();
            return;
        }

        _view.AppendLog("[warn] No completion marker — sending correction...", "error");
        var correction = await SendMessageAsync(
            "You did NOT include a completion marker. You MUST end with one of:\n" +
            "- USER_INPUT_REQUIRED: <question>\n" +
            "- AGENT_IDLE\n" +
            "- AGENT_COMPLETED");
        await EvaluatePromptResult(correction);
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

        var markerText = GetMarkerText(result);

        if (markerText.Contains("USER_INPUT_REQUIRED"))
        {
            var marker = "USER_INPUT_REQUIRED:";
            var idx = markerText.LastIndexOf(marker);
            var question = idx >= 0
                ? markerText[(idx + marker.Length)..].Trim()
                : "";
            AskUser(question);
            return HordeOutcome.UserStopped;
        }

        if (markerText.Contains("TASK_FAILED"))
        {
            var marker = "TASK_FAILED:";
            var idx = markerText.LastIndexOf(marker);
            var reason = idx >= 0
                ? markerText[(idx + marker.Length)..].Trim()
                : "Agent reported failure";
            _view.AppendLog($"[horde] Task failed: {reason}", "error");
            await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, _agentId, reason);
            return HordeOutcome.Failed;
        }

        if (markerText.Contains("TASK_COMPLETED"))
        {
            _tasksCompleted++;
            _view.SetStat(StatField.Tasks, _tasksCompleted.ToString());
            _view.AppendLog("[horde] Task completed.", "result");
            await _mcp.CompleteTaskAsync(_args.PoolId!, _currentTaskId!, _agentId, result.MarkedResult ?? result.ResultText);
            return HordeOutcome.Completed;
        }

        return HordeOutcome.NoMarker;
    }

    private async Task DispatchNextTaskAsync()
    {
        while (true)
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
                await _mcp.PoolUpdateStateAsync(_args.PoolId!, _agentId, "working", $"Task: {title}");

                // First task: send full prompt (MCP header + comm + task)
                // Subsequent tasks: just the task description (agent already knows the rules)
                string taskMessage;
                if (!_hordeInitialPromptSent)
                {
                    taskMessage = PromptBuilder.BuildTaskPrompt(
                        title!, description!, _mcp.Port!.Value, _agentId, _args.PoolId!, _args.LeadId);
                    _hordeInitialPromptSent = true;
                }
                else
                {
                    taskMessage = $"## New Task: {title}\n\n{description}\n\n" +
                                  "Previous task is complete. Focus on this new task now.";
                }

                var result = await SendMessageAsync(taskMessage);
                var outcome = await EvaluateHordeResult(result);

                if (outcome == HordeOutcome.UserStopped) return;

                if (outcome == HordeOutcome.NoMarker && _markerRetries == 0)
                {
                    _markerRetries++;
                    _view.AppendLog("[horde] No completion marker — sending correction...", "error");

                    // Send correction in same session — no relaunch!
                    var correctionResult = await SendMessageAsync(
                        "You did NOT include a completion marker in your previous response. " +
                        "You MUST end with one of:\n- TASK_COMPLETED\n- TASK_FAILED: <reason>");
                    var retryOutcome = await EvaluateHordeResult(correctionResult);

                    if (retryOutcome == HordeOutcome.UserStopped) { _markerRetries = 0; return; }
                    if (retryOutcome == HordeOutcome.NoMarker)
                    {
                        _view.AppendLog("[horde] Still no marker after correction — failing task", "error");
                        await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, _agentId,
                            "No completion marker after correction attempt");
                    }
                }

                _markerRetries = 0;
                continue;
            }

            // All done
            if (resp?.TryGetProperty("all_done", out var ad) == true && ad.GetBoolean())
            {
                _view.AppendLog($"[horde] All tasks done ({_tasksCompleted} completed).", "sys");
                await _mcp.PoolUnregisterAsync(_args.PoolId!, _agentId);
                CompleteWithCountdown();
                return;
            }

            // Blocked
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
            return;
        }
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

    private static string GetMarkerText(RunResult result) =>
        !string.IsNullOrEmpty(result.ResultText) ? result.ResultText : result.LastTextBlock;

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    private static string FormatTokens(int tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:F1}M" : $"{tokens / 1000}k";
}
