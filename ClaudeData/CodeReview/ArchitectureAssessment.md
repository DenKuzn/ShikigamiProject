# Architecture Assessment — Shikigami Project

**Date:** 2026-03-29
**Rules applied:** `~/.claude/agent-rules/Design-Code/*` (architecture-base, design-classes, design-methods, design-parameters, design-conditionals, design-nesting, design-loops, design-robustcode, design-process)

---

## Summary

The project is a well-structured .NET 9 rewrite of a Python MCP server + runner system. The overall decomposition (Core/Server/Runner) is clean, and several design decisions demonstrate solid engineering judgment. However, there are pervasive issues with type safety (magic strings as status values), one God class (MainWindow), and inconsistent error handling that will compound as the project grows.

---

## What Works Well

### 1. Clean Solution Decomposition
The three-project split (Core = domain, Server = transport, Runner = GUI) follows the Decomposition Protocol correctly. Dependencies are acyclic: Runner → Core (via HTTP, not direct reference), Server → Core. This allows each layer to change independently.

**Rule satisfied:** architecture-base §DECOMPOSITION PROTOCOL — acyclic dependency graph at every level.

### 2. RunnerState Enum Replacing Booleans
`MainWindow.cs:20-31` — The `RunnerState` enum explicitly replaces 7 implicit booleans (`_running`, `_waitingInput`, `_inputIsStop`, `_idle`, `_hordeWaiting`, etc.). The comment even explains the rationale. This is a textbook application of the Multi-Bool State Machine rule.

**Rule satisfied:** architecture-base §Multi-Bool State Machine Smell — one enum, all states named, no invalid combinations.

### 3. MessageQueue as ADT
`MessageQueue.cs` — Encapsulates `List<MessageRecord>` + lock behind `Enqueue()`/`DrainAll()`/`Count`. Callers never see the lock. This is a good example of Information Hiding: the secret (thread-safe list management) is completely hidden behind the interface.

**Rule satisfied:** design-classes §ADT, architecture-base §INFORMATION HIDING.

### 4. PromptBuilder Template Externalization
`PromptBuilder.cs` — Templates loaded from external files with built-in fallback defaults. This correctly isolates the volatile area (prompt wording) behind a stable interface (the Build method). Changing prompts requires no recompilation.

**Rule satisfied:** architecture-base §CHANGE ANTICIPATION MAP — volatile business rules isolated in one place.

### 5. Single-Responsibility Services
`PidMonitor`, `IdGenerator`, `PoolService` each have a clear, single purpose. Each passes the SRP test: "what does it do?" can be answered in one phrase without "and".

### 6. ShikigamiContextMemory Task Scoping
`ShikigamiContextMemory.cs` — `BeginTask()`/`CurrentTaskJson()` elegantly scopes horde history per task while preserving full history. Clean separation of concerns.

---

## Should Fix (Medium Priority)

### S1. Magic Strings for All Status Values
**Severity:** High (borderline Critical)
**Locations:** Every file that checks or sets status — `PoolService.cs`, `ShikigamiState.cs`, `AgentEndpoints.cs`, `PoolEndpoints.cs`, `ShikigamiMcpTools.cs`, `MainWindow.xaml.cs`, `PoolRecord.cs`, `TaskRecord.cs`, `PoolAgentInfo`.

Status values are raw strings scattered across the entire codebase:
- Task: `"pending"`, `"in_progress"`, `"completed"`, `"failed"`
- Pool: `"in_progress"`, `"completed"`, `"aborted"`
- Agent: `"working"`, `"waiting"`, `"completed"`, `"failed"`, `"idle"`, `"dead"`, `"starting"`, `"registered"`

**Violation:** architecture-base §INFORMATION HIDING ("literal 100 scattered everywhere → const"), §Bool vs Enum ("Status with 2+ possible states → enum"), §CHANGE ANTICIPATION MAP ("status variables → enum + accessor").

**Risk:** A single typo (`"Pending"` vs `"pending"`) silently breaks the entire pool system. No compiler protection. Adding a new status requires grep-and-pray across all files.

**Fix:** Create enums: `TaskStatus`, `PoolStatus`, `AgentStatus`. Use `[JsonConverter]` for JSON serialization if HTTP compatibility is needed. This is the single highest-ROI change.

### S2. `Dictionary<string, object>` as Return Type
**Locations:** `LaunchService.LaunchPromptAgent()`, `LaunchService.LaunchPool()`, all MCP tools via JSON serialization.

`LaunchService` returns `Dictionary<string, object>` for both success and error cases. The caller must check `result.ContainsKey("error")` — a semantic contract the compiler cannot enforce.

**Violation:** design-classes §ADT ("classes become bad data bags"), architecture-base §INTERFACE DESIGN RULE.

**Fix:** Create typed result records:
```csharp
public record LaunchResult(string AgentId, int Pid, int Port);
public record LaunchError(string Message);
// Or use a discriminated union / Result<T,E> pattern
```

### S3. Dates Stored as Strings
**Locations:** `AgentRecord.RegisteredAt`, `PoolRecord.CreatedAt`, `TaskRecord.CreatedAt`/`StartedAt`/`CompletedAt`, etc.

All timestamps are `string` fields initialized with `DateTime.UtcNow.ToString("o")`. This loses type safety: you cannot compare dates, sort by date, or compute durations without parsing back.

**Violation:** design-parameters §Type Safety ("Choose the type that makes invalid states unrepresentable").

**Fix:** Use `DateTime` or `DateTimeOffset` internally. Serialize to ISO 8601 only at the HTTP/JSON boundary.

### S4. CliRunner.Run() — Deep Nesting and Length
**Location:** `CliRunner.cs:73-234` — ~160 lines, with a `switch` inside `foreach` inside `switch` inside `while` (4+ levels).

**Violation:** design-nesting §Core Rule (max 3 levels), design-methods §Length (caution above 200 LOC, but complexity metrics matter more).

**Fix:** Extract the event parsing into a separate method (`ParseStreamEvent`). The `while/ReadLine` loop becomes a simple dispatcher.

### S5. No Input Validation at HTTP Barricade
**Locations:** `AgentEndpoints.cs`, `PoolEndpoints.cs`.

HTTP endpoints call `data.GetProperty("prompt_id").GetString()!` directly. If the field exists but is null, or the wrong JSON type, this throws an unhandled `InvalidOperationException`. Some endpoints validate required fields (registration), others don't (messaging, cost update).

**Violation:** design-robustcode §Input Validation ("Every function validates its inputs"), §Barricades ("Validation classes at the boundary form a wall").

**Fix:** Consistent validation at every HTTP endpoint entry point. Public methods validate; internal methods can assume clean data.

### S6. RunnerViewModel Is Dead Code
**Location:** `RunnerViewModel.cs` — 48 lines of unused ViewModel. `MainWindow` manages its own state directly, never referencing this class.

**Violation:** design-process §Desirable Characteristics — Leanness ("Can you remove any part without losing functionality?").

**Fix:** Delete it, or commit to MVVM and wire it up. Currently it's speculative code that adds confusion.

### S7. Inconsistent Error Swallowing
**Locations:**
- `LaunchService.LaunchTaskAgent()` line 160: `catch { return null; }` — all diagnostic info lost
- `McpHttpClient.RequestAsync()` line 63: `catch { return null; }` — network errors invisible
- `MainWindow.PollMessagesAsync()` line 577: `catch { }` — polling failures silently ignored
- `CliRunner.Kill()` line 65: `catch { ... } catch { }` — double empty catch
- `PidMonitor.IsPidAlive()` line 69: `catch { return false; }` — PID check failures treated as "dead"

**Violation:** design-robustcode §Error Handling Strategies ("Error handling strategy is an ARCHITECTURE decision"), §Exceptions ("Never leave empty catch blocks").

**Fix:** Decide on one strategy at the architecture level. For a consumer application (game tooling), Robustness is appropriate: log warnings and continue. But silent swallowing is not robustness — it's negligence. At minimum, add `Console.Error.WriteLine` to every catch block.

### S8. Thread Safety Gaps in PoolRecord
**Location:** `PoolRecord.cs`

- `TaskOrder` is `List<string>` — not thread-safe, but read from HTTP endpoints while pool creation writes to it
- `Trash` is `List<TrashEntry>` — manually locked in `PoolToTrash` but other reads (e.g., serialization in StatusWindow) are unprotected

**Violation:** design-robustcode §Core Principle ("If a method receives bad data, it must not corrupt the system").

**Fix:** Use `ConcurrentBag<string>` for TaskOrder (or lock consistently), and lock all Trash access points. Alternatively, make pool creation atomic (build the record fully, then publish it to the concurrent dictionary).

---

## Critical (Must Fix Before Growth)

### C1. MainWindow Is a God Class
**Location:** `MainWindow.xaml.cs` — 817 lines, 20+ fields, fan-out ~15 classes.

This single class handles:
- CLI process orchestration (launch, kill, evaluate results)
- Horde task dispatch and retry logic
- MCP message polling and response
- UI event handling (input, scroll, zoom, buttons)
- State machine transitions (9 states)
- Timer management (4 distinct timers)
- Prompt building delegation
- Context memory management
- Auto-close countdown logic

**Violation:** design-classes §Classes to Avoid ("God class — knows everything, does everything"), architecture-base §COUPLING RULES (fan-out > 7 → decompose), §UNIVERSAL PRINCIPLES — Miller's Rule (20+ fields exceeds 7±2).

**Why this is critical:** Every new feature (prompt editor button, improved horde idle/backoff, any UI addition) requires modifying this one file. One change to CLI orchestration risks breaking the timer logic. The blast radius of any modification is the entire 800-line file. This is the #1 bottleneck for growth.

**Fix:** Extract responsibilities into focused classes:
- `PromptModeOrchestrator` — prompt-mode launch/evaluate/relaunch cycle
- `HordeModeOrchestrator` — horde task dispatch, retry, poll logic
- `RunnerUiController` — input enable/disable, log append, scroll, zoom
- Keep MainWindow as a thin dispatcher that wires services to UI events

### C2. No Consistent Error Handling Architecture
**Across the project.**

Three incompatible error reporting patterns exist simultaneously:
1. `Dictionary<string, object>` with `["error"]` key (LaunchService)
2. JSON string with `{ "error": "..." }` (MCP tools, HTTP endpoints)
3. `null` return (McpHttpClient, LaunchTaskAgent)
4. Exception throwing (direct `GetProperty` calls in endpoints)

**Violation:** design-robustcode §Error Handling Strategies ("Error handling strategy is an ARCHITECTURE decision, not a per-function decision").

**Why this is critical:** When two subsystems use different error patterns, the boundary between them silently drops errors. For example, `LaunchTaskAgent` returns `null` on failure, and `LaunchPool` just calls `continue` — the pool launches with missing agents and no diagnostic. As more features are added, these silent drops will create increasingly mysterious failures.

**Fix:** Choose ONE pattern for the entire project. For a consumer application, recommended approach:
- Internal APIs: use `Result<T>` type (success or error with message)
- External boundaries (HTTP, MCP): convert to JSON error response at the boundary
- Background operations: log to `Console.Error` (already used in PidMonitor — make it universal)

### C3. Status Magic Strings Are a Ticking Bomb
(Elevated from S1 due to blast radius)

This is listed in both sections because while each individual string comparison seems harmless, the aggregate effect is a system where:
- 6 files share implicit knowledge of the exact string values
- No refactoring tool can safely rename a status
- HTTP API, MCP tools, business logic, and UI all embed the same strings independently
- The pool completion logic (`CheckPoolCompletion`) makes critical decisions based on string equality

If horde idle/backoff needs a new task status (e.g., `"retrying"`), you must find and update every comparison across 6+ files, with zero compiler help. This is the definition of "change that must NOT propagate through the interface" — but currently does.

---

## Metrics Summary

| Metric | Target | Actual | Status |
|--------|--------|--------|--------|
| Max nesting depth | ≤3 | 4-5 (CliRunner.Run, PoolEndpoints send) | Needs work |
| Class fan-out | ≤7 | ~15 (MainWindow) | Critical |
| Data members per class | ≤7±2 | 20+ (MainWindow), 10 (PoolRecord) | Critical / Borderline |
| Method length | ≤200 LOC | ~160 (CliRunner.Run), ~100 (DispatchNextTask) | Borderline |
| Cyclomatic complexity | ≤10 | ~12-15 (DispatchNextTaskAsync, LaunchPassAsync) | Needs work |
| Acyclic dependencies | Yes | Yes | Good |
| Consistent error handling | Required | No | Critical |
| Status as enums | Required | No (all strings) | Critical |

---

## Recommended Priority Order

1. **Status enums** (C3/S1) — Highest ROI, protects every future change
2. **Error handling architecture** (C2/S7) — Choose one pattern, apply everywhere
3. **MainWindow decomposition** (C1) — Extract orchestrators, reduce fan-out
4. **HTTP barricade validation** (S5) — Prevent crash on malformed input
5. **Thread safety in PoolRecord** (S8) — Prevent data corruption
6. **Typed result objects** (S2) — Replace Dictionary<string, object>
7. **DateTime fields** (S3) — Type safety for timestamps
8. **CliRunner nesting** (S4) — Extract event parser
9. **Delete RunnerViewModel** (S6) — Remove dead code
