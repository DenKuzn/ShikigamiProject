using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shikigami.Runner.Services;
using Shikigami.Runner.Theme;

namespace Shikigami.Runner;

public partial class MainWindow : Window
{
    /// <summary>
    /// Explicit state machine replacing 7 implicit booleans
    /// (_running, _waitingInput, _inputIsStop, _idle, _hordeWaiting, + _closeTimer-as-state).
    /// Every state is mutually exclusive — no invalid combinations possible.
    /// </summary>
    private enum RunnerState
    {
        Starting,
        Working,
        WaitingInputQuestion,   // USER_INPUT_REQUIRED detected
        WaitingInputStop,       // user pressed Stop, awaiting correction
        Idle,                   // AGENT_IDLE received
        HordeWaiting,           // blocked tasks, polling for availability
        Completing,             // countdown to auto-close
        Completed,
        Aborted,
    }

    private readonly AppArgs _args;
    private readonly McpHttpClient _mcp;
    private readonly CliRunner _cli;
    private readonly DispatcherTimer _dotTimer;
    private readonly DispatcherTimer _mcpPollTimer;

    private bool _dotOn;
    private bool _autoScroll = true;
    private double _logFontSize = 12;
    private int _iteration;
    private int _toolCount;
    private double _totalCost;
    private int _tasksCompleted;
    private RunnerState _state = RunnerState.Starting;
    private bool _userStopped;      // transient signal: Stop pressed during Working
    private bool _keepActive;       // orthogonal toggle: prevent auto-close on complete
    private DispatcherTimer? _closeTimer;
    private int _closeCountdown;
    private string? _originalPrompt;
    private PromptBuilder? _promptBuilder;
    private readonly ShikigamiContextMemory _memory = new();

    // Horde mode
    private readonly bool _taskMode;
    private string? _currentTaskId;
    private DispatcherTimer? _hordePollTimer;
    private int _markerRetries;
    private string? _currentTaskPrompt;

    public MainWindow(AppArgs args)
    {
        InitializeComponent();

        // Fast tooltips (100ms) — set on root element via code for reliable inheritance
        ToolTipService.SetInitialShowDelay(this, 100);
        ToolTipService.SetBetweenShowDelay(this, 0);

        _args = args;
        _mcp = new McpHttpClient(args.McpPort);
        _cli = new CliRunner(args.Agent, args.Model, args.Tools, args.Workdir, args.Effort);
        _taskMode = args.TaskMode;

        var name = args.Agent ?? args.Model ?? "shikigami";
        Title = $"\u2b21 {name}";
        HeaderName.Text = name;
        Icon = EmojiIcon.Create();

        if (_taskMode)
            TasksPanel.Visibility = Visibility.Visible;

        // Dot pulse animation
        _dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _dotTimer.Tick += (_, _) =>
        {
            _dotOn = !_dotOn;
            if (_state is RunnerState.WaitingInputQuestion or RunnerState.WaitingInputStop)
                DotIndicator.Fill = _dotOn ? DeepSpaceTheme.AmberBrush : DeepSpaceTheme.AmberDimBrush;
            else if (_state is RunnerState.Idle or RunnerState.HordeWaiting)
                DotIndicator.Fill = _dotOn ? DeepSpaceTheme.GreenBrush : DeepSpaceTheme.GreenDimBrush;
            else
                DotIndicator.Fill = _dotOn ? DeepSpaceTheme.TealBrush : DeepSpaceTheme.TealDimBrush;
        };
        _dotTimer.Start();

        // MCP message polling
        _mcpPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _mcpPollTimer.Tick += async (_, _) => await PollMessagesAsync();
        _mcpPollTimer.Start();

        LogScroller.PreviewMouseWheel += OnLogMouseWheel;
        LogScroller.ScrollChanged += OnLogScrollChanged;

        Loaded += async (_, _) => await StartAsync();
        Closing += (_, _) => Shutdown();
    }

    private async Task StartAsync()
    {
        await _mcp.ValidatePortAsync();

        if (_taskMode)
        {
            await _mcp.PoolRegisterAsync(_args.PoolId!, _args.AgentId ?? _args.PromptId!, _args.AgentType!);
            AppendLog("[shikigami] Registered in pool, requesting tasks...", "sys");
            await DispatchNextTaskAsync();
        }
        else
        {
            // Fetch prompt from server
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

            AppendLog("[prompt] " + _promptBuilder.FullPromptDisplay(), "prompt");
            await LaunchPassAsync();
        }
    }

    // ── CLI pass helpers ──

    private void BeginCliPass()
    {
        _state = RunnerState.Working;
        _userStopped = false;
        HeaderStatus.Text = "working";
        HeaderStatus.Foreground = DeepSpaceTheme.TealBrush;
        StopButton.IsEnabled = true;
        StopButton.Opacity = 1.0;
    }

    private void FinishCliPass(RunResult result)
    {
        _memory.FlushEvents(result.Events, _iteration);

        if (result.Cost.HasValue)
        {
            _totalCost += result.Cost.Value;
            StatCost.Text = $"${_totalCost:F4}";
            _ = _mcp.SubmitCostAsync(_totalCost);
        }

        // State remains Working — caller sets the next state after evaluating the result
        StopButton.IsEnabled = false;
        StopButton.Opacity = 0.25;
    }

    private async Task<RunResult> RunCliPassAsync(string prompt)
    {
        BeginCliPass();
        var result = await Task.Run(() => _cli.Run(prompt, (type, data) =>
        {
            Dispatcher.Invoke(() => HandleEvent(type, data));
        }));
        FinishCliPass(result);
        return result;
    }

    // ── Horde marker evaluation ──

    private enum HordeOutcome { Completed, Failed, Error, NoMarker, UserStopped }

    private async Task<HordeOutcome> EvaluateHordeResult(RunResult result)
    {
        var agentId = _args.AgentId ?? _args.PromptId!;

        if (_userStopped)
        {
            _userStopped = false;
            AskUserAfterStop();
            return HordeOutcome.UserStopped;
        }

        if (result.Error != null)
        {
            AppendLog($"[horde] Task failed (CLI error): {result.Error}", "error");
            await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, agentId, result.Error);
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
            AppendLog($"[horde] Task failed: {reason}", "error");
            await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, agentId, reason);
            return HordeOutcome.Failed;
        }

        if (result.ResultText.Contains("TASK_COMPLETED"))
        {
            _tasksCompleted++;
            StatTasks.Text = _tasksCompleted.ToString();
            AppendLog($"[horde] Task completed: {Truncate(result.ResultText, 200)}", "result");
            await _mcp.CompleteTaskAsync(_args.PoolId!, _currentTaskId!, agentId, result.ResultText);
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

    // ── Prompt mode ──

    private async Task LaunchPassAsync()
    {
        if (_promptBuilder == null) return;
        if (_state == RunnerState.Idle) ExitIdle();

        _iteration++;
        StatIteration.Text = _iteration.ToString();
        await _mcp.UpdateStateAsync("working", $"Iteration {_iteration}");

        var builtPrompt = _promptBuilder.Build(_iteration, _iteration > 1 ? _memory.ToJson() : null);
        var result = await RunCliPassAsync(builtPrompt);

        // User pressed Stop
        if (_userStopped)
        {
            _userStopped = false;
            AskUserAfterStop();
            return;
        }

        // Check markers in order
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
            AppendLog($"[done] Result: {Truncate(result.ResultText, 300)}", "result");
            _ = _mcp.SubmitLogAsync(result.Events, result.ResultText);
            EnterIdle();
            return;
        }

        if (result.ResultText.Contains("AGENT_COMPLETED"))
        {
            AppendLog($"[done] Result: {Truncate(result.ResultText, 300)}", "result");
            _ = _mcp.SubmitLogAsync(result.Events, result.ResultText);
            CompleteWithCountdown();
            return;
        }

        // No marker — re-launch for correction
        AppendLog("[warn] No completion marker — re-launching for correction...", "error");
        await LaunchPassAsync();
    }

    // ── Horde mode ──

    private async Task DispatchNextTaskAsync()
    {
        var agentId = _args.AgentId ?? _args.PromptId!;
        StopHordePoll();

        var resp = await _mcp.RequestTaskAsync(_args.PoolId!, _args.AgentType!, agentId);

        // Pool aborted
        if (resp?.TryGetProperty("error", out var errProp) == true
            && errProp.GetString() == "Pool aborted")
        {
            AppendLog($"[horde] Pool aborted ({_tasksCompleted} done).", "error");
            await _mcp.PoolUnregisterAsync(_args.PoolId!, agentId);
            _state = RunnerState.Aborted;
            HeaderStatus.Text = "aborted";
            HeaderStatus.Foreground = DeepSpaceTheme.RedBrush;
            return;
        }

        // Got a task
        if (resp?.TryGetProperty("task", out var taskProp) == true
            && taskProp.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            var task = taskProp;
            _currentTaskId = task.GetProperty("id").GetString();
            var title = task.GetProperty("title").GetString();
            var description = task.GetProperty("description").GetString();

            _memory.BeginTask(_currentTaskId!);

            AppendLog($"[horde] Task: {title}", "task");
            _iteration++;
            StatIteration.Text = _iteration.ToString();
            await _mcp.PoolUpdateStateAsync(_args.PoolId!, agentId, "working", $"Task: {title}");

            _currentTaskPrompt = PromptBuilder.BuildTaskPrompt(
                title!, description!, _mcp.Port!.Value, agentId, _args.PoolId!, _args.LeadId);

            var result = await RunCliPassAsync(_currentTaskPrompt);
            var outcome = await EvaluateHordeResult(result);

            if (outcome == HordeOutcome.UserStopped) return;

            if (outcome == HordeOutcome.NoMarker && _markerRetries == 0)
            {
                _markerRetries++;
                AppendLog("[horde] No completion marker — re-launching for correction...", "error");
                _iteration++;
                StatIteration.Text = _iteration.ToString();

                var correctionPrompt = BuildHordePromptWithHistory(
                    "You did NOT include a completion marker in your previous response. " +
                    "You MUST end with one of:\n- TASK_COMPLETED\n- TASK_FAILED: <reason>");

                var retryResult = await RunCliPassAsync(correctionPrompt);
                var retryOutcome = await EvaluateHordeResult(retryResult);

                if (retryOutcome == HordeOutcome.UserStopped) { _markerRetries = 0; return; }
                if (retryOutcome == HordeOutcome.NoMarker)
                {
                    AppendLog("[horde] Still no marker after correction — failing task", "error");
                    await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, agentId,
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
            AppendLog($"[horde] All tasks done ({_tasksCompleted} completed).", "sys");
            await _mcp.PoolUnregisterAsync(_args.PoolId!, agentId);
            CompleteWithCountdown();
            return;
        }

        // Blocked — check if our agent_type has pending tasks
        var blockedTypes = new List<string>();
        if (resp?.TryGetProperty("blocked_agent_types", out var btProp) == true
            && btProp.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var bt in btProp.EnumerateArray())
                blockedTypes.Add(bt.GetString() ?? "");
        }

        if (blockedTypes.Contains(_args.AgentType!))
        {
            var blockedCount = resp?.TryGetProperty("blocked_count", out var bc) == true ? bc.GetInt32() : 0;
            AppendLog($"[horde] Waiting ({blockedCount} blocked)...", "sys");
            _state = RunnerState.HordeWaiting;
            HeaderStatus.Text = $"waiting ({blockedCount} blocked)";
            HeaderStatus.Foreground = DeepSpaceTheme.AmberBrush;
            await _mcp.PoolUpdateStateAsync(_args.PoolId!, agentId, "idle", "Waiting for tasks");
            ScheduleHordePoll();
        }
        else
        {
            AppendLog($"[horde] No more tasks for {_args.AgentType} ({_tasksCompleted} done).", "sys");
            await _mcp.PoolUnregisterAsync(_args.PoolId!, agentId);
            CompleteWithCountdown();
        }
    }

    private async Task RelaunchHordeTaskAsync(string suffix)
    {
        if (_currentTaskPrompt == null) return;

        _iteration++;
        StatIteration.Text = _iteration.ToString();
        var agentId = _args.AgentId ?? _args.PromptId!;
        await _mcp.PoolUpdateStateAsync(_args.PoolId!, agentId, "working", "Continuing task");

        var prompt = BuildHordePromptWithHistory(suffix);
        var result = await RunCliPassAsync(prompt);
        var outcome = await EvaluateHordeResult(result);

        if (outcome == HordeOutcome.UserStopped) return;

        if (outcome == HordeOutcome.NoMarker)
        {
            AppendLog("[horde] No completion marker — failing task", "error");
            await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, agentId,
                "No completion marker after user correction");
        }

        await DispatchNextTaskAsync();
    }

    private void ScheduleHordePoll()
    {
        StopHordePoll();
        _hordePollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _hordePollTimer.Tick += async (_, _) =>
        {
            StopHordePoll();
            await DispatchNextTaskAsync();
        };
        _hordePollTimer.Start();
    }

    private void StopHordePoll()
    {
        _hordePollTimer?.Stop();
        _hordePollTimer = null;
    }

    // ── Events & UI ──

    private void HandleEvent(string type, Dictionary<string, object> data)
    {
        switch (type)
        {
            case "system":
                AppendLog($"[system] model={data["model"]}", "sys");
                break;
            case "tool":
                _toolCount++;
                StatTools.Text = _toolCount.ToString();
                var name = data["name"];
                var detail = data.TryGetValue("detail", out var d) ? d : "";
                AppendLog($"  [{_toolCount}] {name}  {detail}", "tool");
                break;
            case "text":
                AppendLog($"  {data["text"]}", "text");
                break;
            case "thinking":
                AppendLog("  (thinking...)", "dim");
                break;
            case "error":
                AppendLog($"  ERROR: {data["message"]}", "error");
                break;
        }
    }

    private void AppendLog(string text, string tag)
    {
        var para = LogBox.Document.Blocks.LastBlock as Paragraph ?? new Paragraph();
        if (para.Inlines.Count > 0 || LogBox.Document.Blocks.Count == 0)
        {
            para = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            LogBox.Document.Blocks.Add(para);
        }

        var run = new Run(text)
        {
            Foreground = tag switch
            {
                "sys" => DeepSpaceTheme.FgDimBrush,
                "tool" => DeepSpaceTheme.CyanBrush,
                "text" => DeepSpaceTheme.FgBrush,
                "prompt" => DeepSpaceTheme.LavenderBrush,
                "result" => DeepSpaceTheme.GreenBrush,
                "error" => DeepSpaceTheme.RedBrush,
                "task" => DeepSpaceTheme.AmberBrush,
                "dim" => DeepSpaceTheme.FgDimBrush,
                _ => DeepSpaceTheme.FgBrush,
            }
        };
        para.Inlines.Add(run);
        if (_autoScroll)
            LogScroller.ScrollToEnd();
    }

    private async Task PollMessagesAsync()
    {
        if (!_mcp.Active || _state == RunnerState.Completing) return;
        try
        {
            var messages = _taskMode
                ? await _mcp.PoolCheckMessagesAsync(_args.PoolId!, _args.AgentId ?? _args.PromptId!)
                : await _mcp.CheckMessagesAsync(_args.PromptId ?? "");

            if (messages.Count == 0) return;

            // Format and display all messages
            var parts = new List<string>();
            foreach (var msg in messages)
            {
                var sender = msg.TryGetProperty("sender_id", out var s) ? s.GetString() : "?";
                var text = msg.TryGetProperty("text", out var t) ? t.GetString() : "";
                parts.Add($"[Message from {sender}]: {text}");
            }
            var combined = string.Join("\n", parts);

            var sep = new string('\u2500', 54);
            AppendLog(sep, "sys");
            AppendLog("  \u2709 MESSAGE RECEIVED:", "task");
            AppendLog($"  {combined}", "text");
            AppendLog(sep, "sys");

            // If not running CLI → inject message and re-launch
            if (_state != RunnerState.Working)
            {
                _memory.AddMessage(combined);

                // Cancel input/idle wait if active
                if (_state is RunnerState.WaitingInputQuestion or RunnerState.WaitingInputStop)
                {
                    DisableInput();
                    InputBox.Clear();
                }
                if (_state == RunnerState.Idle) ExitIdle();

                if (_taskMode && _state == RunnerState.HordeWaiting)
                {
                    // In horde waiting state, message doesn't re-launch — just display
                }
                else if (_taskMode)
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

    // ── State transitions ──

    private void EnterIdle()
    {
        _state = RunnerState.Idle;

        var sep = new string('\u2500', 54);
        AppendLog(sep, "sys");
        AppendLog("  \u23f3 AGENT IDLE — waiting for messages or new instructions", "result");
        AppendLog("  Type below or send a message via MCP.", "sys");
        AppendLog(sep, "sys");

        HeaderStatus.Text = "idle";
        HeaderStatus.Foreground = DeepSpaceTheme.GreenBrush;
        _ = _mcp.UpdateStateAsync("idle", "Waiting for messages");

        EnableInput();
        InputBox.Focus();
    }

    private void ExitIdle()
    {
        // State will transition to Working when BeginCliPass is called by the subsequent launch
        DisableInput();
        InputBox.Clear();
    }

    private void EnableInput()
    {
        InputBox.IsEnabled = true;
        InputBox.Opacity = 1.0;
        SendButton.IsEnabled = true;
        SendButton.Opacity = 1.0;
    }

    private void DisableInput()
    {
        InputBox.IsEnabled = false;
        InputBox.Opacity = 0.35;
        SendButton.IsEnabled = false;
        SendButton.Opacity = 0.35;
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
        _closeCountdown = 10;
        HeaderStatus.Text = $"completed — closing in {_closeCountdown}s";
        HeaderStatus.Foreground = DeepSpaceTheme.GreenBrush;

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _closeTimer.Tick += (_, _) =>
        {
            _closeCountdown--;
            if (_closeCountdown <= 0)
            {
                _closeTimer.Stop();
                _closeTimer = null;
                Close();
            }
            else
            {
                HeaderStatus.Text = $"completed — closing in {_closeCountdown}s";
            }
        };
        _closeTimer.Start();
    }

    // ── UI event handlers ──

    private void KeepActiveButton_Click(object sender, RoutedEventArgs e)
    {
        _keepActive = !_keepActive;
        if (_keepActive)
        {
            KeepActiveButton.Content = "\u25c9 \u7d50\u754c\u7dad\u6301";
            KeepActiveButton.Background = DeepSpaceTheme.TealDimBrush;
            KeepActiveButton.Foreground = DeepSpaceTheme.TealBrush;
            KeepActiveButton.BorderBrush = DeepSpaceTheme.TealBrush;

            // Cancel pending close and enter idle
            if (_closeTimer != null)
            {
                _closeTimer.Stop();
                _closeTimer = null;
                EnterIdle();
            }
        }
        else
        {
            KeepActiveButton.Content = "\u25ce \u7d50\u754c\u7dad\u6301";
            KeepActiveButton.Background = DeepSpaceTheme.BgPanelBrush;
            KeepActiveButton.Foreground = DeepSpaceTheme.FgDimBrush;
            KeepActiveButton.BorderBrush = DeepSpaceTheme.FgDimBrush;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state != RunnerState.Working) return;
        _userStopped = true;
        StopButton.IsEnabled = false;
        StopButton.Opacity = 0.5;
        StopButton.Content = "\u505c\u6b62\u4e2d...";
        _cli.Kill();
    }

    private void AskUserAfterStop()
    {
        _state = RunnerState.WaitingInputStop;
        StopButton.IsEnabled = false;
        StopButton.Opacity = 0.25;
        StopButton.Content = "\u25a0 \u505c\u6b62";

        var sep = new string('\u2500', 54);
        AppendLog(sep, "sys");
        AppendLog("  \u26a0 STOPPED BY USER", "error");
        AppendLog("  Enter correction or instructions below:", "task");
        AppendLog(sep, "sys");

        HeaderStatus.Text = "stopped — awaiting input";
        HeaderStatus.Foreground = DeepSpaceTheme.AmberBrush;
        _ = _mcp.UpdateStateAsync("waiting", "Stopped by user, awaiting correction");

        EnableInput();
        InputBox.Focus();
    }

    private void AskUser(string question)
    {
        _state = RunnerState.WaitingInputQuestion;
        var sep = new string('\u2500', 54);
        AppendLog(sep, "sys");
        AppendLog("  AGENT ASKS:", "task");
        if (!string.IsNullOrEmpty(question))
            AppendLog($"  {question}", "task");
        AppendLog(sep, "sys");

        HeaderStatus.Text = "awaiting input";
        HeaderStatus.Foreground = DeepSpaceTheme.AmberBrush;
        _ = _mcp.UpdateStateAsync("waiting", $"USER_INPUT_REQUIRED: {Truncate(question, 100)}");

        EnableInput();
        InputBox.Focus();
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                // Ctrl+Enter → insert newline at caret
                var caret = InputBox.CaretIndex;
                InputBox.Text = InputBox.Text.Insert(caret, Environment.NewLine);
                InputBox.CaretIndex = caret + Environment.NewLine.Length;
                e.Handled = true;
            }
            else
            {
                // Enter → send
                SendInput();
                e.Handled = true;
            }
        }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendInput();

    private async void SendInput()
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        InputBox.Clear();
        DisableInput();

        var isStop = _state == RunnerState.WaitingInputStop;

        var sep = new string('\u2500', 54);
        AppendLog(sep, "sys");
        AppendLog(isStop ? "  YOUR CORRECTION:" : "  YOUR ANSWER:", "result");
        AppendLog($"  {text}", "result");
        AppendLog(sep, "sys");

        if (isStop)
            _memory.AddUserStop(text);
        else
            _memory.AddUserInput(text);

        if (_taskMode)
            await RelaunchHordeTaskAsync(isStop
                ? $"\n\nUser stopped you and instructed:\n{text}\n\nApply the correction and complete the task."
                : $"\n\nUser answered:\n{text}\n\nContinue the task.");
        else
            await LaunchPassAsync();
    }

    // ── Scroll & zoom ──

    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // At bottom (within 10px tolerance) → enable auto-scroll
        _autoScroll = LogScroller.VerticalOffset + LogScroller.ViewportHeight >= LogScroller.ExtentHeight - 10;
    }

    private void OnLogMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        _logFontSize = Math.Clamp(_logFontSize + (e.Delta > 0 ? 1 : -1), 6, 30);
        LogBox.FontSize = _logFontSize;
        e.Handled = true;
    }

    // ── Lifecycle ──

    private void Shutdown()
    {
        _dotTimer.Stop();
        _mcpPollTimer.Stop();
        StopHordePoll();
        _cli.Kill();
        if (_taskMode)
            _ = _mcp.PoolUnregisterAsync(_args.PoolId!, _args.AgentId ?? _args.PromptId!);
        else
            _ = _mcp.UnregisterAsync();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
