namespace Shikigami.Runner.Services;

/// <summary>
/// Contract between RunnerSession (presenter) and MainWindow (view).
/// The session calls these methods to update the display.
/// All methods are fire-and-forget UI commands — the session never asks the view for state.
/// </summary>
public interface IRunnerView
{
    // ── Log ──
    void AppendLog(string text, string tag);
    void AppendCollapsible(string header, string body, string headerTag, string bodyTag = "text");

    // ── Header ──
    void SetHeaderStatus(string text, StatusColor color);

    // ── Stats bar ──
    void SetStat(StatField field, string value);

    // ── Input panel ──
    void EnableInput();
    void DisableInput();
    void ClearInput();
    void FocusInput();

    // ── Stop button ──
    void SetStopButton(bool enabled, double opacity, string? text = null);

    // ── Keep Active visual ──
    void SetKeepActiveVisual(bool active);

    // ── Tasks panel (horde) ──
    void ShowTasksPanel();

    // ── Timers (view owns DispatcherTimer, session decides when) ──
    void StartCloseCountdown(int seconds);
    void CancelCloseCountdown();
    void ScheduleHordePoll();
    void StopHordePoll();

    // ── Window lifecycle ──
    void CloseWindow();
}

public enum StatusColor { Teal, Amber, Green, Red }

public enum StatField { Iteration, Tools, Cost, Tasks }
