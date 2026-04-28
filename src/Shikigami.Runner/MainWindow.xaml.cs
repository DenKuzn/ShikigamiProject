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

/// <summary>
/// Thin WPF shell. Implements IRunnerView, owns timers and XAML controls,
/// forwards user actions to RunnerSession.
/// </summary>
public partial class MainWindow : Window, IRunnerView
{
    private readonly RunnerSession _session;
    private readonly DispatcherTimer _dotTimer;
    private readonly DispatcherTimer _mcpPollTimer;
    private bool _stopArmed;
    private DispatcherTimer? _stopArmTimer;
    private DispatcherTimer? _stopBlinkTimer;
    private DispatcherTimer? _hordePollTimer;
    private DispatcherTimer? _closeTimer;
    private bool _dotOn;
    private bool _autoScroll = true;
    private bool _polling;
    private double _logFontSize = 12;
    private readonly List<FrameworkElement> _collapsibleTextBlocks = new();
    private readonly Dictionary<string, (TextBlock arrow, TextBlock header, StackPanel body)> _subagentBlocks = new();
    private int _closeCountdown;

    public MainWindow(AppArgs args)
    {
        InitializeComponent();
        PositionWindow();

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

        // MCP message polling (guard against overlapping ticks)
        _mcpPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _mcpPollTimer.Tick += async (_, _) =>
        {
            if (_polling) return;
            _polling = true;
            try { await _session.PollMessagesAsync(); }
            finally { _polling = false; }
        };
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

        var brush = tag switch
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

        // Split by \r\n, \n, \r — Run does not render \n as a line break in FlowDocument,
        // so multi-line input would otherwise collapse into a single visible line.
        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, System.StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) para.Inlines.Add(new LineBreak());
            para.Inlines.Add(new Run(lines[i]) { Foreground = brush });
        }

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

        var bodyBlock = new TextBox
        {
            Text = body,
            Foreground = GetTagBrush(bodyTag),
            FontFamily = new FontFamily(DeepSpaceTheme.FontMono),
            FontSize = _logFontSize,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(16, 2, 0, 2),
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
        };

        headerPanel.MouseLeftButtonDown += (_, e) =>
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
            e.Handled = true;
        };

        // The body TextBox lives inside a BlockUIContainer of the parent RichTextBox.
        // Clicking it makes the RichTextBox's TextEditor reposition its caret to the
        // BlockUIContainer's anchor (effectively position 0) and raise RequestBringIntoView,
        // which scrolls the outer LogScroller to the top — breaking text selection.
        // Snapshot the outer offset on entry and restore it after the input pass.
        void PreserveOuterScroll()
        {
            var savedOffset = LogScroller.VerticalOffset;
            var wasAutoScroll = _autoScroll;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                LogScroller.ScrollToVerticalOffset(savedOffset);
                _autoScroll = wasAutoScroll;
            }), DispatcherPriority.Render);
        }
        bodyBlock.PreviewMouseLeftButtonDown += (_, _) => PreserveOuterScroll();
        bodyBlock.GotKeyboardFocus += (_, _) => PreserveOuterScroll();

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

    public void BeginSubagentBlock(string id, string header, string headerTag)
    {
        if (_subagentBlocks.ContainsKey(id)) return;

        var arrowBlock = new TextBlock
        {
            Text = "▾ ",
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
            Background = Brushes.Transparent,
        };
        headerPanel.Children.Add(arrowBlock);
        headerPanel.Children.Add(headerBlock);

        // Body is a vertical StackPanel of per-line TextBlocks.
        // Adding TextBlocks to a StackPanel reliably re-flows the parent BlockUIContainer
        // (unlike mutating a single TextBox.Text after it's been parented to a FlowDocument).
        var bodyBlock = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Visibility = Visibility.Visible,
            Margin = new Thickness(16, 2, 0, 2),
        };

        headerPanel.MouseLeftButtonDown += (_, e) =>
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
            e.Handled = true;
        };

        var container = new StackPanel();
        container.Children.Add(headerPanel);
        container.Children.Add(bodyBlock);

        _collapsibleTextBlocks.Add(arrowBlock);
        _collapsibleTextBlocks.Add(headerBlock);

        var block = new BlockUIContainer(container) { Margin = new Thickness(0, 1, 0, 1) };
        LogBox.Document.Blocks.Add(block);

        _subagentBlocks[id] = (arrowBlock, headerBlock, bodyBlock);

        if (_autoScroll)
            LogScroller.ScrollToEnd();
    }

    public void AppendToSubagentBlock(string id, string text, string tag)
    {
        if (!_subagentBlocks.TryGetValue(id, out var entry))
        {
            // Fallback: block missing — surface the text in main log so it isn't lost.
            AppendLog($"  [orphan sub-agent {id[..Math.Min(8, id.Length)]}] {text}", tag);
            return;
        }

        var brush = GetTagBrush(tag);
        // One TextBlock per logical line — multi-line input gets multiple children.
        var lines = text.Split(new[] { "\r\n", "\n", "\r" }, System.StringSplitOptions.None);
        foreach (var line in lines)
        {
            var lineBlock = new TextBlock
            {
                Text = line,
                Foreground = brush,
                FontFamily = new FontFamily(DeepSpaceTheme.FontMono),
                FontSize = _logFontSize,
                TextWrapping = TextWrapping.Wrap,
            };
            entry.body.Children.Add(lineBlock);
            _collapsibleTextBlocks.Add(lineBlock);
        }

        if (_autoScroll)
            LogScroller.ScrollToEnd();
    }

    public void UpdateSubagentBlockHeader(string id, string newHeader, string headerTag)
    {
        if (!_subagentBlocks.TryGetValue(id, out var entry)) return;
        entry.header.Text = newHeader;
        entry.header.Foreground = GetTagBrush(headerTag);
        entry.arrow.Foreground = GetTagBrush(headerTag);
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
        DisarmStop();
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

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_stopArmed)
        {
            ArmStop();
            return;
        }

        DisarmStop();
        _session.OnStopClicked();
    }

    private void ArmStop()
    {
        _stopArmed = true;
        StopButton.Content = "⚠ 停止!";
        StopButton.Background = new SolidColorBrush(Color.FromRgb(0x3D, 0x05, 0x05));
        StopButton.BorderBrush = DeepSpaceTheme.RedBrush;
        StopButton.ToolTip = "Click again to STOP";

        // Blink animation
        bool blinkOn = true;
        _stopBlinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _stopBlinkTimer.Tick += (_, _) =>
        {
            blinkOn = !blinkOn;
            StopButton.Foreground = blinkOn
                ? DeepSpaceTheme.RedBrush
                : new SolidColorBrush(Color.FromRgb(0x80, 0x20, 0x20));
            StopButton.Background = blinkOn
                ? new SolidColorBrush(Color.FromRgb(0x3D, 0x05, 0x05))
                : new SolidColorBrush(Color.FromRgb(0x25, 0x02, 0x02));
        };
        _stopBlinkTimer.Start();

        // Auto-disarm after 3 seconds
        _stopArmTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _stopArmTimer.Tick += (_, _) => DisarmStop();
        _stopArmTimer.Start();
    }

    private void DisarmStop()
    {
        _stopArmed = false;
        _stopBlinkTimer?.Stop();
        _stopBlinkTimer = null;
        _stopArmTimer?.Stop();
        _stopArmTimer = null;

        StopButton.Content = "■ 停止";
        StopButton.Foreground = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        StopButton.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x05, 0x05));
        StopButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        StopButton.ToolTip = "Stop — kill the running CLI process";
    }

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
        foreach (var el in _collapsibleTextBlocks)
        {
            if (el is TextBlock tb) tb.FontSize = _logFontSize;
            else if (el is TextBox tx) tx.FontSize = _logFontSize;
        }
        e.Handled = true;
    }

    // ════════════════════════════════════════════════════════════════
    //  Internal helpers
    // ════════════════════════════════════════════════════════════════

    private void PositionWindow()
    {
        const double offset = 100;

        // Count other Runner processes (excluding ourselves)
        var currentPid = Environment.ProcessId;
        var currentName = Process.GetCurrentProcess().ProcessName;
        int others = 0;
        try
        {
            foreach (var p in Process.GetProcessesByName(currentName))
            {
                if (p.Id != currentPid) others++;
                p.Dispose();
            }
        }
        catch { /* access denied — fall back to 0 */ }

        // Start from center screen, then cascade by number of existing runners
        var screen = SystemParameters.WorkArea;
        var centerX = (screen.Width - Width) / 2;
        var centerY = (screen.Height - Height) / 2;

        Left = centerX + offset * others;
        Top = centerY + offset * others;

        // Clamp so the window doesn't go off-screen
        if (Left + Width > screen.Right)
            Left = screen.Left + offset * (others % 3);
        if (Top + Height > screen.Bottom)
            Top = screen.Top + offset * (others % 3);
    }

    private void StopHordePollInternal()
    {
        _hordePollTimer?.Stop();
        _hordePollTimer = null;
    }
}
