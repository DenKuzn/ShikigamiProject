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
    private double _logFontSize = 10;
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
    private string? _originalPrompt;
    private PromptBuilder? _promptBuilder;
    private List<Dictionary<string, object>> _allEvents = new();

    // Horde mode
    private readonly bool _taskMode;
    private string? _currentTaskId;

    public MainWindow(AppArgs args)
    {
        InitializeComponent();

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
            else if (_idle)
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
        StopButton.Visibility = Visibility.Visible;
        await _mcp.UpdateStateAsync("working", $"Iteration {_iteration}");

        var builtPrompt = _promptBuilder.Build(_iteration, _allEvents);

        await Task.Run(() =>
        {
            var result = _cli.Run(builtPrompt, (type, data) =>
            {
                Dispatcher.Invoke(() => HandleEvent(type, data));
            });

            Dispatcher.Invoke(() =>
            {
                _allEvents.AddRange(result.Events);

                if (result.Cost.HasValue)
                {
                    _totalCost += result.Cost.Value;
                    StatCost.Text = $"${_totalCost:F4}";
                    _ = _mcp.SubmitCostAsync(_totalCost);
                }

                _running = false;
                StopButton.Visibility = Visibility.Collapsed;

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

                AppendLog($"[done] Result: {Truncate(result.ResultText, 300)}", "result");
                _ = _mcp.SubmitLogAsync(result.Events, result.ResultText);

                if (_keepActive)
                {
                    EnterIdle();
                }
                else
                {
                    HeaderStatus.Text = "completed";
                    HeaderStatus.Foreground = DeepSpaceTheme.GreenBrush;
                    _ = _mcp.UpdateStateAsync("completed");
                    AppendLog("[shikigami] Closing in 10 seconds...", "sys");

                    _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                    _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); _closeTimer = null; Close(); };
                    _closeTimer.Start();
                }
            });
        });
    }

    private async Task DispatchNextTaskAsync()
    {
        var agentId = _args.AgentId ?? _args.PromptId!;
        var resp = await _mcp.RequestTaskAsync(_args.PoolId!, _args.AgentType!, agentId);

        if (resp == null || (resp.Value.TryGetProperty("task", out var taskProp) && taskProp.ValueKind == System.Text.Json.JsonValueKind.Null))
        {
            if (resp?.TryGetProperty("all_done", out var ad) == true && ad.GetBoolean())
            {
                AppendLog("[horde] All tasks done. Exiting.", "sys");
                await _mcp.PoolUnregisterAsync(_args.PoolId!, agentId);
                HeaderStatus.Text = "completed";
                HeaderStatus.Foreground = DeepSpaceTheme.GreenBrush;
                return;
            }
            // Blocked — wait and retry
            AppendLog("[horde] No available tasks, waiting...", "sys");
            await _mcp.PoolUpdateStateAsync(_args.PoolId!, agentId, "idle", "Waiting for tasks");
            await Task.Delay(5000);
            await DispatchNextTaskAsync();
            return;
        }

        var task = resp.Value.GetProperty("task");
        _currentTaskId = task.GetProperty("id").GetString();
        var title = task.GetProperty("title").GetString();
        var description = task.GetProperty("description").GetString();

        AppendLog($"[horde] Task: {title}", "task");
        await _mcp.PoolUpdateStateAsync(_args.PoolId!, agentId, "working", $"Task: {title}");

        var taskPrompt = PromptBuilder.BuildTaskPrompt(
            title!, description!, _mcp.Port!.Value, agentId, _args.PoolId!, _args.LeadId);
        _running = true;
        _userStopped = false;
        _iteration++;
        StatIteration.Text = _iteration.ToString();
        HeaderStatus.Text = "working";
        HeaderStatus.Foreground = DeepSpaceTheme.TealBrush;
        StopButton.Visibility = Visibility.Visible;

        await Task.Run(() =>
        {
            var result = _cli.Run(taskPrompt, (type, data) =>
            {
                Dispatcher.Invoke(() => HandleEvent(type, data));
            });

            Dispatcher.Invoke(async () =>
            {
                if (result.Cost.HasValue)
                {
                    _totalCost += result.Cost.Value;
                    StatCost.Text = $"${_totalCost:F4}";
                    _ = _mcp.SubmitCostAsync(_totalCost);
                }

                _tasksCompleted++;
                StatTasks.Text = _tasksCompleted.ToString();
                _running = false;
                StopButton.Visibility = Visibility.Collapsed;

                if (_userStopped)
                {
                    _userStopped = false;
                    AskUserAfterStop();
                    return;
                }

                if (result.Error != null)
                {
                    AppendLog($"[horde] Task failed: {result.Error}", "error");
                    await _mcp.FailTaskAsync(_args.PoolId!, _currentTaskId!, agentId, result.Error);
                }
                else
                {
                    AppendLog($"[horde] Task completed: {Truncate(result.ResultText, 200)}", "result");
                    await _mcp.CompleteTaskAsync(_args.PoolId!, _currentTaskId!, agentId, result.ResultText);
                }

                // Request next task
                await DispatchNextTaskAsync();
            });
        });
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
        if (!_mcp.Active) return;
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
                _allEvents.Add(new Dictionary<string, object>
                {
                    ["type"] = "mcp_message",
                    ["text"] = combined,
                    ["time"] = DateTime.Now.ToString("HH:mm:ss"),
                });

                // Cancel input/idle wait if active
                if (_waitingInput)
                {
                    _waitingInput = false;
                    InputPanel.Visibility = Visibility.Collapsed;
                    InputBox.Clear();
                }
                if (_idle) ExitIdle();

                await LaunchPassAsync();
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

    private void KeepActiveButton_Click(object sender, RoutedEventArgs e)
    {
        _keepActive = !_keepActive;
        if (_keepActive)
        {
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
        StopButton.Content = "STOPPING...";
        _cli.Kill();
    }

    private void AskUserAfterStop()
    {
        _waitingInput = true;
        _inputIsStop = true;
        StopButton.IsEnabled = true;
        StopButton.Content = "■ STOP";

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

        _allEvents.Add(new Dictionary<string, object>
        {
            ["type"] = isStop ? "user_stop" : "user_input",
            ["text"] = text,
            ["time"] = DateTime.Now.ToString("HH:mm:ss"),
        });

        _waitingInput = false;
        await LaunchPassAsync();
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
        _cli.Kill();
        if (_taskMode)
            _ = _mcp.PoolUnregisterAsync(_args.PoolId!, _args.AgentId ?? _args.PromptId!);
        else
            _ = _mcp.UnregisterAsync();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
