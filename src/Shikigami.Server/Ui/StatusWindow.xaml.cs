using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Shikigami.Core.State;

namespace Shikigami.Server.Ui;

public partial class StatusWindow : Window
{
    private readonly ShikigamiState _state;
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _dotTimer;
    private bool _dotOn;

    // Brushes for agent list
    private static readonly SolidColorBrush TealBrush = Frozen("#00e5c0");
    private static readonly SolidColorBrush FgBrush = Frozen("#b8c5d6");
    private static readonly SolidColorBrush FgDimBrush = Frozen("#4a5a6e");
    private static readonly SolidColorBrush GreenBrush = Frozen("#7dff7d");
    private static readonly SolidColorBrush AmberBrush = Frozen("#e5a000");
    private static readonly SolidColorBrush RedBrush = Frozen("#ff5c5c");
    private static readonly SolidColorBrush TealDimBrush = Frozen("#005c4d");

    public StatusWindow(ShikigamiState state)
    {
        InitializeComponent();
        _state = state;

        // Dot pulse
        _dotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _dotTimer.Tick += (_, _) =>
        {
            _dotOn = !_dotOn;
            DotIndicator.Fill = _dotOn ? TealBrush : TealDimBrush;
        };
        _dotTimer.Start();

        // Live refresh every 1s
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _updateTimer.Tick += (_, _) => Refresh();
        _updateTimer.Start();

        Closing += (_, e) => { e.Cancel = true; Hide(); };

        Refresh();
    }

    private void Refresh()
    {
        // Port
        PortLabel.Text = _state.HttpPort > 0 ? $":{_state.HttpPort}" : ":...";

        // Stats
        var active = _state.Agents.Values.Where(a => a.Active).ToList();
        var totalMsgs = _state.Queues.Values.Sum(q => { lock (q) return q.Count; });
        var resultsCount = _state.Agents.Values.Count(a => a.Result != null);
        var logsCount = _state.Agents.Values.Count(a => a.EventLog != null);

        StatAgents.Text = active.Count.ToString();
        StatPrompts.Text = _state.Prompts.Count.ToString();
        StatMessages.Text = totalMsgs.ToString();
        StatResults.Text = resultsCount.ToString();
        StatLogs.Text = logsCount.ToString();
        StatTrash.Text = _state.Trash.Count.ToString();

        // Cost
        var cost = _state.TotalCost;
        CostLabel.Text = cost > 0 ? $"${cost:F4}" : "$0.00";
        var billed = _state.Agents.Values.Count(a => a.CostUsd > 0);
        foreach (var pool in _state.Pools.Values)
            billed += pool.Agents.Values.Count(a => a.CostUsd > 0);
        CostDetailLabel.Text = billed > 0 ? $"{billed} shikigami billed" : "";

        // Pools
        if (_state.Pools.IsEmpty)
        {
            PoolsSection.Visibility = Visibility.Collapsed;
        }
        else
        {
            PoolsSection.Visibility = Visibility.Visible;
            PoolsHeader.Text = $"HORDE POOLS ({_state.Pools.Count})";
            RefreshPools();
        }

        // Agent list
        RefreshAgentList(active);
    }

    private void RefreshPools()
    {
        PoolsList.Items.Clear();
        foreach (var pool in _state.Pools.Values)
        {
            var tasks = pool.Tasks.Values.ToList();
            var total = tasks.Count;
            var done = tasks.Count(t => t.Status is "completed" or "failed");
            var statusColor = pool.Status switch
            {
                "in_progress" => TealBrush,
                "completed" => GreenBrush,
                "aborted" => RedBrush,
                _ => FgDimBrush,
            };

            var panel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(6, 2, 0, 2) };
            panel.Children.Add(MakeText($"{pool.Name}  ", Frozen("#5ec4ff"), 9, true));
            panel.Children.Add(MakeText($"{done}/{total}  ", TealBrush, 8));
            panel.Children.Add(MakeText(pool.Status, statusColor, 8));
            PoolsList.Items.Add(panel);
        }
    }

    private void RefreshAgentList(List<Core.Models.AgentRecord> active)
    {
        var doc = AgentList.Document;
        doc.Blocks.Clear();

        if (active.Count == 0)
        {
            var p = new Paragraph(new Run("(none)") { Foreground = FgDimBrush }) { Margin = new Thickness(0) };
            doc.Blocks.Add(p);
            return;
        }

        foreach (var a in active)
        {
            var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
            p.Inlines.Add(new Run(a.Id) { Foreground = TealBrush });
            p.Inlines.Add(new Run($"  {a.Name}  ") { Foreground = FgBrush });

            var status = a.Status ?? "registered";
            var statusBrush = status switch
            {
                "working" => TealBrush,
                "waiting" => AmberBrush,
                "completed" => GreenBrush,
                "failed" => RedBrush,
                _ => FgDimBrush,
            };
            p.Inlines.Add(new Run(status) { Foreground = statusBrush });

            if (a.CostUsd > 0)
                p.Inlines.Add(new Run($"  ${a.CostUsd:F4}") { Foreground = AmberBrush });

            doc.Blocks.Add(p);
        }
    }

    private static System.Windows.Controls.TextBlock MakeText(string text, SolidColorBrush fg, double size, bool bold = false)
    {
        return new System.Windows.Controls.TextBlock
        {
            Text = text,
            Foreground = fg,
            FontFamily = new FontFamily("Consolas"),
            FontSize = size,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        };
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
