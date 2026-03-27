using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Shikigami.Runner.ViewModels;

/// <summary>
/// ViewModel for the main Runner window. Binds to UI elements.
/// </summary>
public sealed class RunnerViewModel : INotifyPropertyChanged
{
    private string _title = "Shikigami";
    private string _status = "starting";
    private int _iteration;
    private int _toolCount;
    private double _totalCost;
    private int _tasksCompleted;
    private bool _isRunning;
    private string _agentName = "";

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public int Iteration { get => _iteration; set => Set(ref _iteration, value); }
    public int ToolCount { get => _toolCount; set => Set(ref _toolCount, value); }
    public double TotalCost { get => _totalCost; set => Set(ref _totalCost, value); }
    public int TasksCompleted { get => _tasksCompleted; set => Set(ref _tasksCompleted, value); }
    public bool IsRunning { get => _isRunning; set => Set(ref _isRunning, value); }
    public string AgentName { get => _agentName; set => Set(ref _agentName, value); }

    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    public void AddLog(string text, string tag = "default")
    {
        LogEntries.Add(new LogEntry(text, tag));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed record LogEntry(string Text, string Tag);
