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
    private bool _running;
    private bool _waitingInput;
    private bool _userStopped;
    private bool _inputIsStop;
    private bool _idle;
    private bool _keepActive;
    private DispatcherTimer? _closeTimer;
    private int _closeCountdown;
    private string? _originalPrompt;
    private PromptBuilder? _promptBuilder;
    private readonly ShikigamiContextMemory _memory = new();

    // Horde mode
    private readonly bool _taskMode;
    private string? _currentTaskId;
    private bool _hordeWaiting;
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
            if (_waitingInput)
                DotIndicator.Fill = _dotOn ? DeepSpaceTheme.AmberBrush : DeepSpaceTheme.AmberDimBrush;
            else if (_idle || _hordeWaiting)
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

    private async Task LaunchPassAsync()
    {
        if (_promptBuilder == null) return;
        if (_idle) ExitIdle();
        _running = true;
        _userStopped = false;
        _iteration++;
        StatIteration.Text = _iteration.ToString();
        HeaderStatus.Text = "working";
        HeaderStatus.Foreground = DeepSpaceTheme.TealBrush;
        StopButton.IsEnabled = true;
        StopButton.Opacity = 1.0;
        await _mcp.UpdateStateAsync("working", $"Iteration {_iteration}");

        var builtPrompt = _promptBuilder.Build(_iteration, _iteration > 1 ? _memory.ToJson() : null);

        await Task.Run(() =>
        {
            var result = _cli.Run(builtPrompt, (type, data) =>
            {
                Dispatcher.Invoke(() => HandleEvent(type, data));
            });

            Dispatcher.Invoke(async () =>
            {
                _memory.FlushEvents(result.Events, _iteration);

                if (result.Cost.HasValue)
                {
                    _totalCost += result.Cost.Value;
                    StatCost.Text = $"${_totalCost:F4}";
                    _ = _mcp.SubmitCostAsync(_totalCost);
                }

                _running = false;
                StopButton.IsEnabled = false;
                StopButton.Opacity = 0.25;

                // User pressed Stop → show input panel for correction
                if (_userStopped)
                {
                    _userStopped = false;
                    AskUserAfterStop();
                    return;
                }

                // Check for USER_INPUT_REQUIRED marker
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

                // Check for AGENT_IDLE marker
                if (result.ResultText.Contains("AGENT_IDLE"))
                {
                    AppendLog($"[done] Result: {Truncate(result.ResultText, 300)}", "result");
                    _ = _mcp.SubmitLogAsync(result.Events, result.ResultText);
                    EnterIdle();
                    return;
                }

                // Check for AGENT_COMPLETED marker
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
            });
        });
    }

    private async Task DispatchNextTaskAsync()
    {
        var agentId = _args.AgentId ?? _args.PromptId!;
        StopHordePoll();
        _hordeWaiting = false;

        var resp = await _mcp.RequestTaskAsync(_args.PoolId!, _args.AgentType!, agentId);

        // Pool aborted
        if (resp?.TryGetProperty("error", out var errProp) == true
            && errProp.GetString() == "Pool aborted")
        {
            AppendLog($"[horde] Pool aborted ({_tasksCompleted} done).", "error");
            await _mcp.PoolUnregisterAsync(_args.PoolId!, agentId);
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
            await _mcp.PoolUpdateStateAsync(_args.PoolId!, agentId, "working", $"Task: {title}");

            var taskPrompt = PromptBuilder.BuildTaskPrompt(
                title!, description!, _mcp.Port!.Value, agentId, _args.PoolId!, _args.LeadId);
            _currentTaskPrompt = taskPrompt;
            _running = true;
            _userStopped = false;
            _iteration++;
            StatIteration.Text = _iteration.ToString();
            HeaderStatus.Text = "working";
            HeaderStatus.Foreground = DeepSpaceTheme.TealBrush;
            StopButton.IsEnabled = true;
            StopButton.Opacity = 1.0;

            await Task.Run(() =>
            {
                var result = _cli.Run(taskPrompt, (type, data) =>
                {
                    Dispatcher.Invoke(() => HandleEvent(type, data));
                });

                Dispatcher.Invoke(async () =>
                {
                    _memory.FlushEvents(result.Events, _iteration);

                    if (result.Cost.HasValue)
                    {
                        _totalCost += result.Cost.Value;
                        StatCost.Text = $"${_totalCost:F4}";
                        _ = _mcp.SubmitCostAsync(_totalCost);
                    }

                    _running = false;
                    StopButton.IsEnabled = false;
                    StopButton.Opacity = 0.25;

                    if (_userStopped)
                    {
                        _userStopped = false;
                        AskUserAfterStop();
                        return;
                    }

                    if (result.Error != null)
                    {
                        AppendLog($"[horde] Task failed (CLI error): {result.Error}", "error");
                        await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, agentId, result.Error);
                    }
                    else if (result.ResultText.Contains("TASK_FAILED"))
                    {
                        var marker = "TASK_FAILED:";
                        var idx = result.ResultText.LastIndexOf(marker);
                        var reason = idx >= 0
                            ? result.ResultText[(idx + marker.Length)..].Trim()
                            : "Agent reported failure";
                        AppendLog($"[horde] Task failed: {reason}", "error");
                        await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, agentId, reason);
                    }
                    else if (result.ResultText.Contains("TASK_COMPLETED"))
                    {
                        _tasksCompleted++;
                        StatTasks.Text = _tasksCompleted.ToString();
                        AppendLog($"[horde] Task completed: {Truncate(result.ResultText, 200)}", "result");
                        await _mcp.CompleteTaskAsync(_args.PoolId!, _currentTaskId!, agentId, result.ResultText);
                    }
                    else
                    {
                        // No marker — re-launch once for correction with history
                        if (_markerRetries == 0)
                        {
                            _markerRetries++;
                            AppendLog("[horde] No completion marker — re-launching for correction...", "error");
                            _iteration++;
                            StatIteration.Text = _iteration.ToString();
                            _running = true;
                            StopButton.IsEnabled = true;
                            StopButton.Opacity = 1.0;
                            var historyJson = _memory.CurrentTaskJson();
                            var correctionPrompt = taskPrompt;
                            if (!string.IsNullOrEmpty(historyJson))
                                correctionPrompt += $"\n\n## Full History (current task)\n```json\n{historyJson}\n```";
                            correctionPrompt +=
                                "\n\nYou did NOT include a completion marker in your previous response. " +
                                "You MUST end with one of:\n" +
                                "- TASK_COMPLETED\n" +
                                "- TASK_FAILED: <reason>\n";
                            await Task.Run(() =>
                            {
                                var retryResult = _cli.Run(correctionPrompt, (type2, data2) =>
                                {
                                    Dispatcher.Invoke(() => HandleEvent(type2, data2));
                                });
                                Dispatcher.Invoke(async () =>
                                {
                                    _memory.FlushEvents(retryResult.Events, _iteration);

                                    if (retryResult.Cost.HasValue)
                                    {
                                        _totalCost += retryResult.Cost.Value;
                                        StatCost.Text = $"${_totalCost:F4}";
                                        _ = _mcp.SubmitCostAsync(_totalCost);
                                    }
                                    _running = false;
                                    StopButton.IsEnabled = false;
                                    StopButton.Opacity = 0.25;

                                    if (retryResult.ResultText.Contains("TASK_COMPLETED"))
                                    {
                                        _tasksCompleted++;
                                        StatTasks.Text = _tasksCompleted.ToString();
                                        AppendLog($"[horde] Task completed: {Truncate(retryResult.ResultText, 200)}", "result");
                                        await _mcp.CompleteTaskAsync(_args.PoolId!, _currentTaskId!, agentId, retryResult.ResultText);
                                    }
                                    else if (retryResult.ResultText.Contains("TASK_FAILED"))
                                    {
                                        var m = "TASK_FAILED:";
                                        var i = retryResult.ResultText.LastIndexOf(m);
                                        var r = i >= 0 ? retryResult.ResultText[(i + m.Length)..].Trim() : "Agent reported failure";
                                        AppendLog($"[horde] Task failed: {r}", "error");
                                        await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, agentId, r);
                                    }
                                    else
                                    {
                                        AppendLog("[horde] Still no marker after correction — failing task", "error");
                                        await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, agentId,
                                            "No completion marker after correction attempt");
                                    }
                                    _markerRetries = 0;
                                    await DispatchNextTaskAsync();
                                });
                            });
                            return;
                        }
                    }

                    _markerRetries = 0;
                    await DispatchNextTaskAsync();
                });
            });
            return;
        }

        // All done
        if (resp?.TryGetProperty("all_done", out var ad) == true && ad.GetBoolean())
        {
            AppendLog($"[horde] All tasks done ({_tasksCompleted} completed). Exiting.", "sys");
            await _mcp.PoolUnregisterAsync(_args.PoolId!, agentId);
            HeaderStatus.Text = "completed";
            HeaderStatus.Foreground = DeepSpaceTheme.GreenBrush;
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
            _hordeWaiting = true;
            HeaderStatus.Text = $"waiting ({blockedCount} blocked)";
            HeaderStatus.Foreground = DeepSpaceTheme.AmberBrush;
            await _mcp.PoolUpdateStateAsync(_args.PoolId!, agentId, "idle", "Waiting for tasks");
            ScheduleHordePoll();
        }
        else
        {
            // No pending tasks for our type — we're done
            AppendLog($"[horde] No more tasks for {_args.AgentType} ({_tasksCompleted} done).", "sys");
            await _mcp.PoolUnregisterAsync(_args.PoolId!, agentId);
            HeaderStatus.Text = "completed";
            HeaderStatus.Foreground = DeepSpaceTheme.GreenBrush;
        }
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
                // Don't show full thinking, just indicate it
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
        if (!_mcp.Active || _closeTimer != null) return;
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
            if (!_running)
            {
                _memory.AddMessage(combined);

                // Cancel input/idle wait if active
                if (_waitingInput)
                {
                    _waitingInput = false;
                    InputPanel.Visibility = Visibility.Collapsed;
                    InputBox.Clear();
                }
                if (_idle) ExitIdle();

                if (_taskMode && _hordeWaiting)
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

    private void EnterIdle()
    {
        _idle = true;

        var sep = new string('\u2500', 54);
        AppendLog(sep, "sys");
        AppendLog("  \u23f3 AGENT IDLE — waiting for messages or new instructions", "result");
        AppendLog("  Type below or send a message via MCP.", "sys");
        AppendLog(sep, "sys");

        HeaderStatus.Text = "idle";
        HeaderStatus.Foreground = DeepSpaceTheme.GreenBrush;
        _ = _mcp.UpdateStateAsync("idle", "Waiting for messages");

        InputPanel.Visibility = Visibility.Visible;
        InputBox.Focus();
    }

    private void ExitIdle()
    {
        _idle = false;
        InputPanel.Visibility = Visibility.Collapsed;
        InputBox.Clear();
    }

    private void CompleteWithCountdown()
    {
        if (_keepActive)
        {
            EnterIdle();
            return;
        }

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

    private void KeepActiveButton_Click(object sender, RoutedEventArgs e)
    {
        _keepActive = !_keepActive;
        if (_keepActive)
        {
            KeepActiveButton.Content = "◉ 結界維持";
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
            KeepActiveButton.Content = "◎ 結界維持";
            KeepActiveButton.Background = DeepSpaceTheme.BgPanelBrush;
            KeepActiveButton.Foreground = DeepSpaceTheme.FgDimBrush;
            KeepActiveButton.BorderBrush = DeepSpaceTheme.FgDimBrush;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_running) return;
        _userStopped = true;
        StopButton.IsEnabled = false;
        StopButton.Opacity = 0.5;
        StopButton.Content = "停止中...";
        _cli.Kill();
    }

    private void AskUserAfterStop()
    {
        _waitingInput = true;
        _inputIsStop = true;
        StopButton.IsEnabled = false;
        StopButton.Opacity = 0.25;
        StopButton.Content = "■ 停止";

        var sep = new string('\u2500', 54);
        AppendLog(sep, "sys");
        AppendLog("  \u26a0 STOPPED BY USER", "error");
        AppendLog("  Enter correction or instructions below:", "task");
        AppendLog(sep, "sys");

        HeaderStatus.Text = "stopped — awaiting input";
        HeaderStatus.Foreground = DeepSpaceTheme.AmberBrush;
        _ = _mcp.UpdateStateAsync("waiting", "Stopped by user, awaiting correction");

        InputPanel.Visibility = Visibility.Visible;
        InputBox.Focus();
    }

    private void AskUser(string question)
    {
        _waitingInput = true;
        var sep = new string('\u2500', 54);
        AppendLog(sep, "sys");
        AppendLog("  AGENT ASKS:", "task");
        if (!string.IsNullOrEmpty(question))
            AppendLog($"  {question}", "task");
        AppendLog(sep, "sys");

        HeaderStatus.Text = "awaiting input";
        HeaderStatus.Foreground = DeepSpaceTheme.AmberBrush;
        _ = _mcp.UpdateStateAsync("waiting", $"USER_INPUT_REQUIRED: {Truncate(question, 100)}");

        InputPanel.Visibility = Visibility.Visible;
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
        InputPanel.Visibility = Visibility.Collapsed;

        var isStop = _inputIsStop;
        _inputIsStop = false;

        var sep = new string('\u2500', 54);
        AppendLog(sep, "sys");
        AppendLog(isStop ? "  YOUR CORRECTION:" : "  YOUR ANSWER:", "result");
        AppendLog($"  {text}", "result");
        AppendLog(sep, "sys");

        if (isStop)
            _memory.AddUserStop(text);
        else
            _memory.AddUserInput(text);

        _waitingInput = false;

        if (_taskMode)
            await RelaunchHordeTaskAsync(isStop
                ? $"\n\nUser stopped you and instructed:\n{text}\n\nApply the correction and complete the task."
                : $"\n\nUser answered:\n{text}\n\nContinue the task.");
        else
            await LaunchPassAsync();
    }

    private async Task RelaunchHordeTaskAsync(string suffix)
    {
        if (_currentTaskPrompt == null) return;

        // Build prompt: base task + history (if any) + suffix
        var historyJson = _memory.CurrentTaskJson();
        var parts = new List<string> { _currentTaskPrompt };
        if (!string.IsNullOrEmpty(historyJson))
        {
            parts.Add($"## Full History (current task)\n```json\n{historyJson}\n```");
            parts.Add("Continue from where you left off. " +
                       "Do NOT re-read files you already have in history.");
        }
        parts.Add(suffix.TrimStart('\n'));
        var prompt = string.Join("\n\n", parts);

        _running = true;
        _userStopped = false;
        _iteration++;
        StatIteration.Text = _iteration.ToString();
        HeaderStatus.Text = "working";
        HeaderStatus.Foreground = DeepSpaceTheme.TealBrush;
        StopButton.IsEnabled = true;
        StopButton.Opacity = 1.0;
        var agentId = _args.AgentId ?? _args.PromptId!;
        await _mcp.PoolUpdateStateAsync(_args.PoolId!, agentId, "working", "Continuing task");

        await Task.Run(() =>
        {
            var result = _cli.Run(prompt, (type, data) =>
            {
                Dispatcher.Invoke(() => HandleEvent(type, data));
            });

            Dispatcher.Invoke(async () =>
            {
                _memory.FlushEvents(result.Events, _iteration);

                if (result.Cost.HasValue)
                {
                    _totalCost += result.Cost.Value;
                    StatCost.Text = $"${_totalCost:F4}";
                    _ = _mcp.SubmitCostAsync(_totalCost);
                }

                _running = false;
                StopButton.IsEnabled = false;
                StopButton.Opacity = 0.25;

                if (_userStopped)
                {
                    _userStopped = false;
                    AskUserAfterStop();
                    return;
                }

                if (result.Error != null)
                {
                    AppendLog($"[horde] Task failed (CLI error): {result.Error}", "error");
                    await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, agentId, result.Error);
                }
                else if (result.ResultText.Contains("TASK_FAILED"))
                {
                    var marker = "TASK_FAILED:";
                    var idx = result.ResultText.LastIndexOf(marker);
                    var reason = idx >= 0
                        ? result.ResultText[(idx + marker.Length)..].Trim()
                        : "Agent reported failure";
                    AppendLog($"[horde] Task failed: {reason}", "error");
                    await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, agentId, reason);
                }
                else if (result.ResultText.Contains("TASK_COMPLETED"))
                {
                    _tasksCompleted++;
                    StatTasks.Text = _tasksCompleted.ToString();
                    AppendLog($"[horde] Task completed: {Truncate(result.ResultText, 200)}", "result");
                    await _mcp.CompleteTaskAsync(_args.PoolId!, _currentTaskId!, agentId, result.ResultText);
                }
                else
                {
                    AppendLog("[horde] No completion marker — failing task", "error");
                    await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, agentId,
                        "No completion marker after user correction");
                }

                await DispatchNextTaskAsync();
            });
        });
    }

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
