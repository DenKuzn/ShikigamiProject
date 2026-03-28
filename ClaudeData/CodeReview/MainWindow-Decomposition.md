# MainWindow Decomposition — Architecture Proposal

**Date:** 2026-03-29
**Target:** `src/Shikigami.Runner/MainWindow.xaml.cs` (817 LOC, 20+ fields, fan-out ~15)

---

## 1. Problem Analysis

### 1.1 What MainWindow Currently Does

A line-by-line audit reveals **7 distinct responsibilities** packed into one class:

| # | Responsibility | Lines | Fields Owned | Methods |
|---|---|---|---|---|
| 1 | CLI orchestration | 150-185 | `_cli`, `_userStopped` | `BeginCliPass`, `FinishCliPass`, `RunCliPassAsync` |
| 2 | Prompt-mode pipeline | 109-312 | `_originalPrompt`, `_promptBuilder` | `StartAsync` (prompt path), `LaunchPassAsync` |
| 3 | Horde-mode pipeline | 109-464 | `_currentTaskId`, `_currentTaskPrompt`, `_markerRetries`, `_hordePollTimer`, `_taskMode` | `StartAsync` (horde path), `DispatchNextTaskAsync`, `RelaunchHordeTaskAsync`, `EvaluateHordeResult`, `BuildHordePromptWithHistory`, `ScheduleHordePoll`, `StopHordePoll` |
| 4 | MCP message polling | 523-578 | (uses `_mcp`, `_state`, `_taskMode`) | `PollMessagesAsync` |
| 5 | State machine & transitions | 580-731 | `_state`, `_keepActive`, `_closeTimer`, `_closeCountdown` | `EnterIdle`, `ExitIdle`, `CompleteWithCountdown`, `AskUser`, `AskUserAfterStop` |
| 6 | UI rendering | 468-521, 607-621, 655-798 | `_dotOn`, `_autoScroll`, `_logFontSize`, `_dotTimer` | `HandleEvent`, `AppendLog`, `EnableInput`, `DisableInput`, all Click/Key/Scroll handlers |
| 7 | Stats tracking | scattered | `_iteration`, `_toolCount`, `_totalCost`, `_tasksCompleted` | (inline updates) |

**Total: 23 fields, 35+ methods, 7 responsibilities.**

### 1.2 Why This Matters

From `design-classes §Classes to Avoid`:
> God class — knows everything, does everything. Violates SRP, creates massive coupling. Split into focused classes, each modeling one ADT.

From `architecture-base §UNIVERSAL PRINCIPLES`:
> Fan-out > 7 → class is too complex, decompose.
> Data members > 7±2 → decomposition signal.

Concrete consequences:
- Adding horde idle/backoff requires modifying a file that also handles prompt-mode logic, UI rendering, and scroll zoom. **Blast radius = everything.**
- The prompt editor button (TODO) must be added to the same class that manages CLI processes. **Risk of accidental interaction.**
- Testing any business logic requires instantiating a WPF Window with all its dependencies.

### 1.3 Dependency Map (current)

```
MainWindow
├── AppArgs              (config)
├── McpHttpClient        (HTTP communication)
├── CliRunner            (CLI process management)
├── PromptBuilder        (prompt assembly)
├── ShikigamiContextMemory (history tracking)
├── DeepSpaceTheme       (static brushes)
├── EmojiIcon            (static icon factory)
├── DispatcherTimer      (×4: dot, mcp poll, horde poll, close countdown)
├── RunResult            (CLI pass result)
├── HordeOutcome         (horde evaluation result)
├── RunnerState          (state enum)
└── ~15 XAML controls    (LogBox, HeaderStatus, DotIndicator, StopButton, etc.)
```

Fan-out = **~15** (limit: ≤7).

---

## 2. Proposed Architecture

### 2.1 Pattern: MVP (Model-View-Presenter)

The decomposition follows the **Presenter pattern**, which is the natural fit for WPF code-behind that mixes business logic with UI manipulation:

- **Model** — services that already exist: `McpHttpClient`, `CliRunner`, `PromptBuilder`, `ShikigamiContextMemory`
- **View** — `MainWindow`, reduced to a thin shell that implements `IRunnerView`
- **Presenter** — new `RunnerSession` class that owns the state machine and orchestration logic

```
App.xaml.cs
│
├── creates MainWindow(args)
│   └── creates RunnerSession(args, view: this, mcp, cli)
│       ├── owns RunnerState (state machine)
│       ├── owns PromptBuilder, ShikigamiContextMemory
│       ├── owns RunnerStats (iteration, tools, cost, tasks)
│       ├── calls IRunnerView methods for UI effects
│       └── delegates to McpHttpClient, CliRunner
│
└── MainWindow implements IRunnerView
    ├── owns XAML controls
    ├── owns DispatcherTimer (dot pulse, mcp poll → delegates to session)
    ├── owns scroll/zoom behavior
    └── forwards user actions → session
```

### 2.2 Key Design Decisions

**Q: Why not full MVVM with data binding?**
A: The existing codebase uses code-behind with direct control manipulation (`AppendLog` builds `Paragraph`/`Run` objects, `DotIndicator.Fill` is set imperatively). Converting to MVVM would rewrite the entire View layer and the XAML. MVP preserves all existing UI code in MainWindow and only moves business logic out.

**Q: Why not separate PromptModeOrchestrator + HordeModeOrchestrator?**
A: They share too much state — `_state`, `_memory`, `_mcp`, `_iteration`, `_userStopped`, `_keepActive`, `RunCliPassAsync`, `BeginCliPass`/`FinishCliPass`, `CompleteWithCountdown`, `AskUser`, `AskUserAfterStop`, `PollMessagesAsync`. Splitting them would require either passing 10+ shared references or creating yet another shared-state object. A single RunnerSession with two internal method groups (prompt-mode / horde-mode) is simpler.

**Q: Why not extract the state machine into its own class?**
A: The state transitions are tightly coupled to the actions that trigger them (CLI completion → evaluate markers → enter idle/complete/ask). Separating the state from the actions that read and write it would split one cohesive concept into two classes that constantly call each other. The state machine IS the orchestration logic — it's the RunnerSession's secret.

---

## 3. Interface Design

### 3.1 IRunnerView

This is the contract between the presenter (RunnerSession) and the view (MainWindow). It answers: **"What UI operations does the business logic need?"**

Every method is a command — the presenter tells the view what to do, never asks it for state.

```csharp
/// <summary>
/// Contract for the Runner UI. The presenter (RunnerSession) calls these
/// methods to update the display. The view (MainWindow) implements them
/// with WPF controls.
///
/// WHAT IT HIDES: All WPF control references, XAML element names,
/// brush/color details, layout decisions.
/// </summary>
public interface IRunnerView
{
    // ── Log ──
    void AppendLog(string text, string tag);

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

    // ── Tasks panel (horde) ──
    void ShowTasksPanel();

    // ── Window lifecycle ──
    void CloseWindow();
}

public enum StatusColor { Teal, Amber, Green, Red }

public enum StatField { Iteration, Tools, Cost, Tasks }
```

**Field count in IRunnerView: 0** (it's an interface — no state).
**Method count: 11** — each is a single UI operation.

Note what's NOT in the interface:
- No `DotIndicator`, `LogBox`, `HeaderStatus` — those are XAML implementation details
- No `SolidColorBrush` — the view maps `StatusColor` to theme brushes internally
- No `DispatcherTimer` — timers are the view's mechanism for triggering session callbacks
- No `GetInputText()` — the view pushes user input to the session, the session never pulls

### 3.2 RunnerSession

```csharp
/// <summary>
/// Orchestrates a single shikigami's lifecycle: launch CLI, evaluate results,
/// manage state transitions, handle messages.
///
/// WHAT IT HIDES: The state machine (RunnerState), mode-specific logic
/// (prompt vs horde), marker evaluation, retry strategy, prompt assembly.
///
/// WHAT IT EXPOSES: Commands that the UI can trigger (user input, stop, toggle).
/// </summary>
public sealed class RunnerSession
{
    // ── Dependencies (injected) ──
    private readonly AppArgs _args;
    private readonly IRunnerView _view;
    private readonly McpHttpClient _mcp;
    private readonly CliRunner _cli;

    // ── Owned state ──
    private readonly ShikigamiContextMemory _memory = new();
    private PromptBuilder? _promptBuilder;
    private RunnerState _state = RunnerState.Starting;
    private RunnerStats _stats;
    private bool _userStopped;
    private bool _keepActive;

    // ── Horde-specific (only used when _args.TaskMode) ──
    private string? _originalPrompt;
    private string? _currentTaskId;
    private string? _currentTaskPrompt;
    private int _markerRetries;

    // ── Public API (called by MainWindow) ──
    public Task StartAsync();
    public Task OnUserInput(string text);
    public void OnStopClicked();
    public void OnKeepActiveToggled();
    public Task OnMessagesReceived(List<JsonElement> messages);
    public Task OnHordePollTick();
    public void Shutdown();

    // ── Internal orchestration (private) ──
    // CLI pass: RunCliPassAsync, BeginCliPass, FinishCliPass
    // Prompt mode: LaunchPassAsync
    // Horde mode: DispatchNextTaskAsync, RelaunchHordeTaskAsync,
    //             EvaluateHordeResult, BuildHordePromptWithHistory
    // State transitions: EnterIdle, ExitIdle, CompleteWithCountdown,
    //                    AskUser, AskUserAfterStop
    // Message handling: HandleCliEvent
}

/// <summary>
/// Grouped stats to reduce field count. Passed to view as formatted strings.
/// </summary>
private struct RunnerStats
{
    public int Iteration;
    public int ToolCount;
    public double TotalCost;
    public int TasksCompleted;
}
```

**Field count: 12** (down from 23). Still above 7±2 but justified — 4 are injected dependencies, 4 are horde-specific (never used in prompt mode). Effective working set per mode = ~8.

**Fan-out: 6** — `AppArgs`, `IRunnerView`, `McpHttpClient`, `CliRunner`, `PromptBuilder`, `ShikigamiContextMemory`. Within the ≤7 limit.

### 3.3 MainWindow (after refactoring)

```csharp
/// <summary>
/// Thin WPF shell. Implements IRunnerView, owns timers and XAML controls,
/// forwards user actions to RunnerSession.
///
/// WHAT IT HIDES: XAML control references, WPF brushes, timer plumbing,
/// scroll/zoom behavior, dot pulse animation.
/// </summary>
public partial class MainWindow : Window, IRunnerView
{
    // ── Dependencies ──
    private readonly RunnerSession _session;

    // ── UI-only state ──
    private readonly DispatcherTimer _dotTimer;
    private readonly DispatcherTimer _mcpPollTimer;
    private DispatcherTimer? _hordePollTimer;
    private DispatcherTimer? _closeTimer;
    private bool _dotOn;
    private bool _autoScroll = true;
    private double _logFontSize = 12;
    private int _closeCountdown;
    private bool _keepActiveVisual;

    // ── IRunnerView implementation ──
    // AppendLog, SetHeaderStatus, SetStat, EnableInput, etc.
    // (all existing UI manipulation code moves here unchanged)

    // ── Timer callbacks → delegate to session ──
    // mcpPollTimer.Tick → poll messages → session.OnMessagesReceived(msgs)
    // hordePollTimer.Tick → session.OnHordePollTick()
    // closeTimer.Tick → countdown → CloseWindow()

    // ── User actions → delegate to session ──
    // SendInput → session.OnUserInput(text)
    // StopButton_Click → session.OnStopClicked()
    // KeepActiveButton_Click → session.OnKeepActiveToggled()

    // ── UI-only behavior (stays in Window) ──
    // Dot pulse animation
    // Scroll/zoom (OnLogScrollChanged, OnLogMouseWheel)
    // InputBox_PreviewKeyDown (Ctrl+Enter newline handling)
}
```

**Field count: 10** — borderline, but 5 are timers (a WPF-specific necessity) and the rest are primitive UI state. No business logic fields.

**Fan-out: 4** — `RunnerSession`, `DeepSpaceTheme`, `EmojiIcon`, `AppArgs`. Well within ≤7.

---

## 4. Data Flow

### 4.1 Startup

```
App.OnStartup
  │
  ├── new MainWindow(args)
  │     ├── new McpHttpClient(port)
  │     ├── new CliRunner(agent, model, ...)
  │     ├── new RunnerSession(args, view: this, mcp, cli)
  │     ├── setup timers (dot, mcp poll)
  │     └── Loaded += session.StartAsync()
  │
  └── window.Show()
```

### 4.2 Prompt Mode — Normal Pass

```
session.StartAsync()
  ├── mcp.ValidatePortAsync()
  ├── fetch prompt, build PromptBuilder
  ├── mcp.RegisterAsync(...)
  ├── view.AppendLog("[prompt] ...")
  └── LaunchPassAsync()
        ├── view.SetStat(Iteration, ...)
        ├── RunCliPassAsync(prompt)
        │     ├── BeginCliPass()
        │     │     ├── _state = Working
        │     │     ├── view.SetHeaderStatus("working", Teal)
        │     │     └── view.SetStopButton(enabled: true)
        │     ├── cli.Run(prompt, onEvent)
        │     │     └── onEvent → view.AppendLog (via HandleCliEvent)
        │     └── FinishCliPass(result)
        │           ├── memory.FlushEvents(...)
        │           ├── view.SetStat(Cost, ...)
        │           └── view.SetStopButton(enabled: false)
        └── evaluate result markers
              ├── USER_INPUT_REQUIRED → AskUser(question)
              │     ├── _state = WaitingInputQuestion
              │     ├── view.SetHeaderStatus("awaiting input", Amber)
              │     ├── view.EnableInput()
              │     └── view.FocusInput()
              ├── AGENT_IDLE → EnterIdle()
              ├── AGENT_COMPLETED → CompleteWithCountdown()
              └── no marker → recursive LaunchPassAsync()
```

### 4.3 User Input

```
MainWindow.SendInput()
  ├── text = InputBox.Text
  ├── InputBox.Clear()
  └── session.OnUserInput(text)
        ├── view.DisableInput()
        ├── view.AppendLog("YOUR ANSWER: ...")
        ├── memory.AddUserInput(text)
        └── if taskMode → RelaunchHordeTaskAsync(...)
            else → LaunchPassAsync()
```

### 4.4 Message Polling

```
MainWindow._mcpPollTimer.Tick
  ├── messages = mcp.CheckMessagesAsync(...)
  ├── if empty → return
  └── session.OnMessagesReceived(messages)
        ├── view.AppendLog("MESSAGE RECEIVED: ...")
        ├── memory.AddMessage(combined)
        └── if not Working → re-launch appropriate mode
```

### 4.5 Close Countdown

The close countdown is a **shared concern**: the session decides WHEN to start/cancel it, the view owns the timer mechanics.

```
session.CompleteWithCountdown()
  ├── if _keepActive → EnterIdle(); return
  ├── _state = Completing
  └── view.StartCloseCountdown(seconds: 10, onTick: text =>
  │       view.SetHeaderStatus(text, Green)
  │   , onComplete: () =>
  │       view.CloseWindow()
  │   )

session.OnKeepActiveToggled()
  ├── _keepActive = !_keepActive
  ├── view.SetKeepActiveVisual(_keepActive)
  └── if _keepActive && _state == Completing
        ├── view.CancelCloseCountdown()
        └── EnterIdle()
```

Wait — this is getting complex. Simpler: the session just calls `view.StartCloseCountdown()` and `view.CancelCloseCountdown()`. The view handles the timer, calls `view.CloseWindow()` when countdown reaches zero. If the session needs to cancel (keep active toggled), it calls `view.CancelCloseCountdown()`.

But then the view needs to call the session back if KeepActive is toggled during countdown... which it already does via `session.OnKeepActiveToggled()`.

Let me simplify:

```
RunnerSession:
  CompleteWithCountdown() → _state = Completing; view.StartCloseCountdown(10)
  OnKeepActiveToggled() → if completing, view.CancelCloseCountdown(); EnterIdle()

MainWindow:
  StartCloseCountdown(secs) → create timer, tick updates header, zero → CloseWindow()
  CancelCloseCountdown() → stop timer
```

Clean — the session tells WHEN, the view handles HOW.

---

## 5. Horde Poll Timer Ownership

The horde poll timer is currently in MainWindow and ticks to call `DispatchNextTaskAsync()`. After refactoring:

```
RunnerSession:
  needs_horde_poll → true/false signal when entering/leaving HordeWaiting state
  OnHordePollTick() → public method called by timer

MainWindow:
  ScheduleHordePoll() → create 5s DispatcherTimer → session.OnHordePollTick()
  StopHordePoll() → stop timer
```

The session calls `view.ScheduleHordePoll()` and `view.StopHordePoll()` at the right moments. This requires adding two methods to `IRunnerView`:

```csharp
void ScheduleHordePoll();
void StopHordePoll();
```

Alternatively, the session could own a `System.Threading.Timer` and `Post` to the dispatcher. But since we're already in WPF-land and everything runs on the dispatcher thread, keeping `DispatcherTimer` in the view is simpler.

---

## 6. Complete IRunnerView (Final)

```csharp
public interface IRunnerView
{
    // ── Log ──
    void AppendLog(string text, string tag);

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

    // ── Timers (view owns the DispatcherTimer, session decides when) ──
    void StartCloseCountdown(int seconds);
    void CancelCloseCountdown();
    void ScheduleHordePoll();
    void StopHordePoll();

    // ── Window lifecycle ──
    void CloseWindow();
}
```

**17 methods total.** Each is a single UI operation — no "and" in any description.

---

## 7. Migration Strategy

The refactoring is mechanical — no behavior change, no XAML change, no new features.

### Step 1: Create files

```
src/Shikigami.Runner/
├── Services/
│   ├── IRunnerView.cs        ← NEW (interface + enums)
│   └── RunnerSession.cs      ← NEW (all business logic from MainWindow)
├── MainWindow.xaml            ← UNCHANGED
└── MainWindow.xaml.cs         ← SLIMMED (IRunnerView impl + timers + UI)
```

### Step 2: Extract IRunnerView

Define the interface. Implement it in MainWindow by wrapping existing UI code in the interface methods. At this point MainWindow has BOTH the session logic AND the view implementation.

### Step 3: Extract RunnerSession

Move all non-UI methods from MainWindow to RunnerSession. Replace direct UI manipulation (`HeaderStatus.Text = ...`) with `_view.SetHeaderStatus(...)` calls. Move all business-logic fields to RunnerSession.

### Step 4: Wire up

MainWindow constructor creates RunnerSession, passes `this` as IRunnerView. Timer callbacks delegate to session methods.

### Step 5: Verify

Build. Run in both prompt and horde modes. Behavior must be identical — this is a pure refactoring with no feature changes.

---

## 8. Before/After Summary

| Metric | Before | After (MainWindow) | After (RunnerSession) | Target |
|---|---|---|---|---|
| Lines | 817 | ~250 | ~450 | — |
| Fields | 23 | 10 (UI-only) | 12 (business) | ≤7±2 |
| Fan-out | ~15 | 4 | 6 | ≤7 |
| Responsibilities | 7 | 2 (UI + timer plumbing) | 2 (orchestration + state machine) | 1-2 |
| Testable without WPF? | No | N/A (it IS the WPF part) | Yes (mock IRunnerView) | — |

The field counts are still above the ideal 7±2, but each is now justified:
- MainWindow: 5 timers + 5 primitive UI state = all WPF-necessary, no business logic
- RunnerSession: 4 injected dependencies + 4 core state + 4 horde-specific = grouped by concern

---

## 9. What This Enables

After this refactoring:

1. **Horde idle/backoff** (TODO) — modify only `RunnerSession.DispatchNextTaskAsync()` and `OnHordePollTick()`, zero risk to UI code
2. **Prompt editor button** (TODO) — add to MainWindow + IRunnerView, zero risk to orchestration
3. **Unit testing** — RunnerSession can be tested with a mock IRunnerView that records calls. No WPF required.
4. **Alternative UI** — a console runner could implement IRunnerView and reuse RunnerSession unchanged
5. **Future status enums** (C3 fix) — session owns all status comparisons, single file to update
