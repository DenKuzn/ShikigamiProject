using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
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
    private int _iteration;
    private int _toolCount;
    private double _totalCost;
    private int _tasksCompleted;
    private bool _running;
    private string? _prompt;
    private bool _waitingInput;

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

        if (_taskMode)
            TasksPanel.Visibility = Visibility.Visible;

        // Dot pulse animation
        _dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _dotTimer.Tick += (_, _) =>
        {
            _dotOn = !_dotOn;
            DotIndicator.Fill = _dotOn ? DeepSpaceTheme.TealBrush : DeepSpaceTheme.TealDimBrush;
        };
        _dotTimer.Start();

        // MCP message polling
        _mcpPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _mcpPollTimer.Tick += async (_, _) => await PollMessagesAsync();
        _mcpPollTimer.Start();

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
                    _prompt = tp.GetString();
            }
            _prompt ??= _args.Prompt ?? "No prompt provided.";

            await _mcp.RegisterAsync(
                _args.PromptId ?? "",
                _args.Agent ?? _args.Model ?? "unknown",
                _prompt,
                Process.GetCurrentProcess().Id,
                _args.LeadId);

            AppendLog("[prompt] " + Truncate(_prompt, 200), "prompt");
            await LaunchPassAsync();
        }
    }

    private async Task LaunchPassAsync()
    {
        if (_prompt == null) return;
        _running = true;
        _iteration++;
        StatIteration.Text = _iteration.ToString();
        HeaderStatus.Text = "working";
        HeaderStatus.Foreground = DeepSpaceTheme.TealBrush;
        await _mcp.UpdateStateAsync("working", $"Iteration {_iteration}");

        await Task.Run(() =>
        {
            var result = _cli.Run(_prompt!, (type, data) =>
            {
                Dispatcher.Invoke(() => HandleEvent(type, data));
            });

            Dispatcher.Invoke(() =>
            {
                if (result.Cost.HasValue)
                {
                    _totalCost += result.Cost.Value;
                    StatCost.Text = $"${_totalCost:F4}";
                    _ = _mcp.SubmitCostAsync(_totalCost);
                }

                _running = false;
                HeaderStatus.Text = "completed";
                HeaderStatus.Foreground = DeepSpaceTheme.GreenBrush;
                AppendLog($"[done] Result: {Truncate(result.ResultText, 300)}", "result");

                _ = _mcp.SubmitLogAsync(result.Events, result.ResultText);
                _ = _mcp.UpdateStateAsync("completed");
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

        _prompt = $"Task: {title}\n\n{description}";
        _running = true;
        _iteration++;
        StatIteration.Text = _iteration.ToString();
        HeaderStatus.Text = "working";
        HeaderStatus.Foreground = DeepSpaceTheme.TealBrush;

        await Task.Run(() =>
        {
            var result = _cli.Run(_prompt, (type, data) =>
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

            foreach (var msg in messages)
            {
                var sender = msg.TryGetProperty("sender_id", out var s) ? s.GetString() : "?";
                var text = msg.TryGetProperty("text", out var t) ? t.GetString() : "";
                AppendLog($"  [msg from {sender}] {text}", "sys");
            }
        }
        catch { /* polling failure is not critical */ }
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SendInput();
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendInput();

    private void SendInput()
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        InputBox.Clear();
        // TODO: Feed input back to CLI process (USER_INPUT_REQUIRED protocol)
        AppendLog($"  [input] {text}", "text");
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
