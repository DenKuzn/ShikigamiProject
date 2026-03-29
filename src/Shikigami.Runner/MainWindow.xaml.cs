using System.Windows;
using System.Windows.Documents;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shikigami.Runner.Services;
using Shikigami.Runner.Theme;

namespace Shikigami.Runner;

/// <summary>
/// Thin WPF shell. Implements IRunnerView, owns timers and XAML controls,
/// forwards user actions to RunnerSession.
/// </summary>
public partial class MainWindow : Window, IRunnerView
{
    private readonly RunnerSession _session;
    private readonly DispatcherTimer _dotTimer;
    private readonly DispatcherTimer _mcpPollTimer;
    private DispatcherTimer? _hordePollTimer;
    private DispatcherTimer? _closeTimer;
    private bool _dotOn;
    private bool _autoScroll = true;
    private double _logFontSize = 12;
    private readonly List<TextBlock> _collapsibleTextBlocks = new();
    private int _closeCountdown;

    public MainWindow(AppArgs args)
    {
        InitializeComponent();

        ToolTipService.SetInitialShowDelay(this, 100);
        ToolTipService.SetBetweenShowDelay(this, 0);

        var name = args.Agent ?? args.Model ?? "shikigami";
        Title = $"\u2b21 {name}";
        HeaderName.Text = name;
        Icon = EmojiIcon.Create();

        _session = new RunnerSession(args, this);

        // Dot pulse animation
        _dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _dotTimer.Tick += (_, _) =>
        {
            _dotOn = !_dotOn;
            var color = _session.DotColor;
            DotIndicator.Fill = color switch
            {
                StatusColor.Amber => _dotOn ? DeepSpaceTheme.AmberBrush : DeepSpaceTheme.AmberDimBrush,
                StatusColor.Green => _dotOn ? DeepSpaceTheme.GreenBrush : DeepSpaceTheme.GreenDimBrush,
                _ => _dotOn ? DeepSpaceTheme.TealBrush : DeepSpaceTheme.TealDimBrush,
            };
        };
        _dotTimer.Start();

        // MCP message polling
        _mcpPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _mcpPollTimer.Tick += async (_, _) => await _session.PollMessagesAsync();
        _mcpPollTimer.Start();

        LogScroller.PreviewMouseWheel += OnLogMouseWheel;
        LogScroller.ScrollChanged += OnLogScrollChanged;

        Loaded += async (_, _) => await _session.StartAsync();
        Closing += (_, _) =>
        {
            _dotTimer.Stop();
            _mcpPollTimer.Stop();
            _session.Shutdown();
        };
    }

    // ════════════════════════════════════════════════════════════════
    //  IRunnerView implementation
    // ════════════════════════════════════════════════════════════════

    public void AppendLog(string text, string tag)
    {
        // Convert leading spaces to left margin — consistent for wrapping and \n
        var spaceCount = text.Length - text.TrimStart(' ').Length;
        if (spaceCount > 0)
            text = text.TrimStart(' ');
        var leftMargin = spaceCount * _logFontSize * 0.6;

        var para = LogBox.Document.Blocks.LastBlock as Paragraph;
        if (para == null || para.Inlines.Count > 0 || LogBox.Document.Blocks.Count == 0)
        {
            para = new Paragraph { Margin = new Thickness(leftMargin, 1, 0, 1) };
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

    public void AppendCollapsible(string header, string body, string headerTag, string bodyTag = "text")
    {
        var arrowBlock = new TextBlock
        {
            Text = "▸ ",
            Foreground = GetTagBrush(headerTag),
            FontFamily = new FontFamily(DeepSpaceTheme.FontMono),
            FontSize = _logFontSize,
        };

        var headerBlock = new TextBlock
        {
            Text = header,
            Foreground = GetTagBrush(headerTag),
            FontFamily = new FontFamily(DeepSpaceTheme.FontMono),
            FontSize = _logFontSize,
        };

        var headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent, // enable hit-test on empty space
        };
        headerPanel.Children.Add(arrowBlock);
        headerPanel.Children.Add(headerBlock);

        var bodyBlock = new TextBlock
        {
            Text = body,
            Foreground = GetTagBrush(bodyTag),
            FontFamily = new FontFamily(DeepSpaceTheme.FontMono),
            FontSize = _logFontSize,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(16, 2, 0, 2),
        };

        headerPanel.MouseLeftButtonDown += (_, _) =>
        {
            if (bodyBlock.Visibility == Visibility.Collapsed)
            {
                bodyBlock.Visibility = Visibility.Visible;
                arrowBlock.Text = "▾ ";
            }
            else
            {
                bodyBlock.Visibility = Visibility.Collapsed;
                arrowBlock.Text = "▸ ";
            }
        };

        var container = new StackPanel();
        container.Children.Add(headerPanel);
        container.Children.Add(bodyBlock);

        _collapsibleTextBlocks.Add(arrowBlock);
        _collapsibleTextBlocks.Add(headerBlock);
        _collapsibleTextBlocks.Add(bodyBlock);

        var block = new BlockUIContainer(container) { Margin = new Thickness(0, 1, 0, 1) };
        LogBox.Document.Blocks.Add(block);

        if (_autoScroll)
            LogScroller.ScrollToEnd();
    }

    private SolidColorBrush GetTagBrush(string tag) => tag switch
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
    };

    public void SetHeaderStatus(string text, StatusColor color)
    {
        HeaderStatus.Text = text;
        HeaderStatus.Foreground = color switch
        {
            StatusColor.Teal => DeepSpaceTheme.TealBrush,
            StatusColor.Amber => DeepSpaceTheme.AmberBrush,
            StatusColor.Green => DeepSpaceTheme.GreenBrush,
            StatusColor.Red => DeepSpaceTheme.RedBrush,
            _ => DeepSpaceTheme.FgDimBrush,
        };
    }

    public void SetStat(StatField field, string value)
    {
        switch (field)
        {
            case StatField.Iteration: StatIteration.Text = value; break;
            case StatField.Tools: StatTools.Text = value; break;
            case StatField.Cost: StatCost.Text = value; break;
            case StatField.Tasks: StatTasks.Text = value; break;
            case StatField.Context: StatContext.Text = value; break;
        }
    }

    public void EnableInput()
    {
        InputBox.IsEnabled = true;
        InputBox.Opacity = 1.0;
        SendButton.IsEnabled = true;
        SendButton.Opacity = 1.0;
    }

    public void DisableInput()
    {
        InputBox.IsEnabled = false;
        InputBox.Opacity = 0.35;
        SendButton.IsEnabled = false;
        SendButton.Opacity = 0.35;
    }

    public void ClearInput() => InputBox.Clear();

    public void FocusInput() => InputBox.Focus();

    public void SetStopButton(bool enabled, double opacity, string? text = null)
    {
        StopButton.IsEnabled = enabled;
        StopButton.Opacity = opacity;
        if (text != null) StopButton.Content = text;
    }

    public void SetKeepActiveVisual(bool active)
    {
        if (active)
        {
            KeepActiveButton.Content = "\u25c9 \u7d50\u754c\u7dad\u6301";
            KeepActiveButton.Background = DeepSpaceTheme.TealDimBrush;
            KeepActiveButton.Foreground = DeepSpaceTheme.TealBrush;
            KeepActiveButton.BorderBrush = DeepSpaceTheme.TealBrush;
        }
        else
        {
            KeepActiveButton.Content = "\u25ce \u7d50\u754c\u7dad\u6301";
            KeepActiveButton.Background = DeepSpaceTheme.BgPanelBrush;
            KeepActiveButton.Foreground = DeepSpaceTheme.FgDimBrush;
            KeepActiveButton.BorderBrush = DeepSpaceTheme.FgDimBrush;
        }
    }

    public void ShowTasksPanel() => TasksPanel.Visibility = Visibility.Visible;

    public void StartCloseCountdown(int seconds)
    {
        _closeCountdown = seconds;
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
                SetHeaderStatus($"completed \u2014 closing in {_closeCountdown}s", StatusColor.Green);
            }
        };
        _closeTimer.Start();
    }

    public void CancelCloseCountdown()
    {
        _closeTimer?.Stop();
        _closeTimer = null;
    }

    public void ScheduleHordePoll()
    {
        StopHordePollInternal();
        _hordePollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _hordePollTimer.Tick += async (_, _) =>
        {
            StopHordePollInternal();
            await _session.OnHordePollTick();
        };
        _hordePollTimer.Start();
    }

    public void StopHordePoll() => StopHordePollInternal();

    public void CloseWindow() => Close();

    // ════════════════════════════════════════════════════════════════
    //  UI event handlers → forward to session
    // ════════════════════════════════════════════════════════════════

    private void KeepActiveButton_Click(object sender, RoutedEventArgs e) =>
        _session.OnKeepActiveToggled();

    private void StopButton_Click(object sender, RoutedEventArgs e) =>
        _session.OnStopClicked();

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendInput();

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                var caret = InputBox.CaretIndex;
                InputBox.Text = InputBox.Text.Insert(caret, Environment.NewLine);
                InputBox.CaretIndex = caret + Environment.NewLine.Length;
                e.Handled = true;
            }
            else
            {
                SendInput();
                e.Handled = true;
            }
        }
    }

    private async void SendInput()
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        await _session.OnUserInput(text);
    }

    // ════════════════════════════════════════════════════════════════
    //  Scroll & zoom (view-only behavior)
    // ════════════════════════════════════════════════════════════════

    private void OnLogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _autoScroll = LogScroller.VerticalOffset + LogScroller.ViewportHeight >= LogScroller.ExtentHeight - 10;
    }

    private void OnLogMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        _logFontSize = Math.Clamp(_logFontSize + (e.Delta > 0 ? 1 : -1), 6, 30);
        LogBox.FontSize = _logFontSize;
        foreach (var tb in _collapsibleTextBlocks)
            tb.FontSize = _logFontSize;
        e.Handled = true;
    }

    // ════════════════════════════════════════════════════════════════
    //  Internal helpers
    // ════════════════════════════════════════════════════════════════

    private void StopHordePollInternal()
    {
        _hordePollTimer?.Stop();
        _hordePollTimer = null;
    }
}
