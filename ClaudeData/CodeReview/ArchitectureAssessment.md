# Architecture & Code Assessment — Shikigami Project

**Date:** 2026-03-29
**Scope:** Full codebase — Shikigami.Core, Shikigami.Server, Shikigami.Runner
**Rules applied:** Design-Code rules (architecture-base, design-classes, design-methods, design-parameters, design-conditionals, design-robustcode, design-nesting)

---

## Summary

The project has a solid foundation: clean 3-project separation, sensible domain modeling, working product. However, several architectural decisions — primarily the use of raw strings for status values and untyped dictionaries for data transport — create compounding risks as the codebase grows. The MainWindow is a God class with a hidden state machine made of 7+ booleans.

| Category | Count |
|---|---|
| What works well | 10 |
| Should fix (moderate) | 12 |
| Critical to fix | 4 |

---

## What Works Well

### 1. Clean Project Decomposition
Three projects (Core, Server, Runner) with clear responsibilities:
- Core = domain models + business logic (zero UI/transport dependency)
- Server = MCP + HTTP + dashboard
- Runner = WPF per-shikigami GUI

This passes the Decomposition Protocol test: dependency graph is acyclic (Core ← Server, Core ← Runner; Server and Runner don't depend on each other).

### 2. Focused Domain Models
`AgentRecord`, `TaskRecord`, `MessageRecord`, `PromptRecord` — each represents exactly one ADT. No method bloat, no mixed abstraction levels. Pass the ADT test: "Can you describe ALL public members as serving one purpose in one sentence?"

### 3. Thread-Safe State Store
`ShikigamiState` uses `ConcurrentDictionary` for primary collections and explicit locks for cost accounting. Atomic cost delta calculation (`UpdateAgentCost`, `UpdatePoolAgentCost`) avoids race conditions on the cost counter.

### 4. PoolService Business Logic Isolation
Pool validation, task assignment, cascade failure, and dependency resolution are isolated in `PoolService`. This is a textbook example of information hiding — callers don't need to know the dependency graph traversal algorithm.

### 5. External Prompt Templates
Prompt templates live as `.txt` files next to the executable with built-in fallbacks. Good binding time choice: load-time for templates that designers may edit, compile-time constants as safety net.

### 6. ShikigamiContextMemory Scoping
Horde mode scopes history per task (`BeginTask`/`CurrentTaskJson`), while preserving full history for debugging (`ToJson`). Clean separation of scoped vs. complete context.

### 7. CLI Runner Event Parsing
`CliRunner.Run` cleanly separates process management from event parsing. The `onEvent` callback pattern allows the caller (MainWindow) to handle UI updates without the runner knowing about WPF.

### 8. ViewModel Pattern
`RunnerViewModel` implements `INotifyPropertyChanged` with a clean generic `Set<T>` helper. Properties are correctly separated from the view.

### 9. PID Monitor as Background Service
`PidMonitor.RunAsync` runs independently with `CancellationToken` support. No coupling to UI — it just mutates state, and the dashboard refreshes independently.

### 10. Backward-Compatible API
HTTP endpoints preserve the Python original's contract exactly. External clients (Runners) don't need to know this is a .NET rewrite.

---

## Should Fix (Moderate Priority)

### M1. Magic Strings for Status Values
**Rule violated:** architecture-base §Bool vs Enum, §CHANGE ANTICIPATION MAP

Status values are raw strings throughout the codebase:

| Domain | Values | Files affected |
|---|---|---|
| Task status | `"pending"`, `"completed"`, `"failed"`, `"in_progress"` | PoolService, PoolEndpoints, ShikigamiMcpTools, MainWindow |
| Pool status | `"in_progress"`, `"completed"`, `"aborted"` | PoolRecord, PoolService, PidMonitor, ShikigamiMcpTools, PoolEndpoints |
| Agent state | `"working"`, `"waiting"`, `"completed"`, `"failed"`, `"idle"`, `"dead"`, `"starting"` | MainWindow, AgentEndpoints, StatusWindow |

**Risk:** Adding a new status requires grep-and-fix across the entire codebase. A typo (`"complted"`) is a silent bug — no compiler error.

**Fix:** Create enums `TaskStatus`, `PoolStatus`, `AgentState` in Shikigami.Core. Use `[JsonConverter]` for HTTP/MCP serialization.

### M2. Dictionary<string, object> as Data Transport
**Rule violated:** design-classes §ADT, design-parameters §Type Safety

`LaunchService` returns `Dictionary<string, object>` for both success and error cases:
```csharp
// LaunchService.cs:26
public Dictionary<string, object> LaunchPromptAgent(...)
// LaunchService.cs:90
public Dictionary<string, object> LaunchPool(...)
```

`PoolService.ValidateTasks` and `CreatePool` accept `List<Dictionary<string, object>>` for task data.

**Risk:** No compile-time safety. Caller must know exact key names. Refactoring a key name requires searching strings. `task["id"].ToString()!` can throw `KeyNotFoundException` at runtime.

**Fix:** Create typed DTOs: `LaunchResult`, `LaunchError`, `TaskInput`. Use discriminated union pattern (`Result<TSuccess, TError>`) or separate types.

### M3. Timestamps as Strings
**Rule violated:** design-parameters §Type Safety

Every record stores timestamps as `string`:
```csharp
public string RegisteredAt { get; set; } = DateTime.UtcNow.ToString("o");
public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
```

**Risk:** Cannot sort, compare, or compute duration without parsing. No compiler protection against non-ISO strings being assigned.

**Fix:** Use `DateTimeOffset` internally. Convert to ISO string only at serialization boundaries (JSON output).

### M4. Duplicated Message Drain Pattern
**Rule violated:** design-methods §Why Create (code duplication at 4+ sites)

The "lock → copy → clear → trash" pattern appears in:
- `ShikigamiMcpTools.CheckMessages()` (line 84-99)
- `ShikigamiMcpTools.CheckPoolMessages()` (line 321-337)
- `AgentEndpoints /messages/{agentId}` (line 98-114)
- `PoolEndpoints /pools/{poolId}/messages/check` (line 234-249)

**Fix:** Extract to `ShikigamiState.DrainQueue(string queueId)` that returns `List<MessageRecord>` and handles locking internally.

### M5. ShikigamiState Exposes Public Collections
**Rule violated:** design-classes §Encapsulation

```csharp
public ConcurrentDictionary<string, AgentRecord> Agents { get; } = new();
public ConcurrentDictionary<string, List<MessageRecord>> Queues { get; } = new();
```

Any code can directly manipulate `Agents`, `Queues`, `Prompts`, `Pools`. The `Queues` dictionary stores `List<MessageRecord>` — a non-thread-safe collection inside a concurrent dictionary. Every caller must remember to `lock(queue)`.

**Fix:** Make collections private. Expose domain-level methods: `RegisterAgent(...)`, `EnqueueMessage(...)`, `DrainQueue(...)`, `FindAgent(...)`. Lock management stays inside the state class.

### M6. StatusWindow Duplicates Color Definitions
**Rule violated:** architecture-base §INFORMATION HIDING (same secret in two places)

`StatusWindow.xaml.cs` defines its own color set:
```csharp
private static readonly SolidColorBrush TealBrush = Frozen("#8B5CF6"); // This is actually purple!
```

Meanwhile, `DeepSpaceTheme.cs` in Runner defines different values. The StatusWindow's "Teal" is `#8B5CF6` (purple), while Runner's Teal is `#00E5C0`.

**Fix:** Move shared color definitions to Shikigami.Core or a shared constants file. Both Server and Runner reference the same source.

### M7. Empty catch Blocks
**Rule violated:** design-robustcode §Exceptions ("Never leave empty catch blocks")

Found in:
- `CliRunner.Kill()` (line 66): `catch { }`
- `CliRunner.Run()` (line 147): `catch { continue; }`
- `LaunchService.LaunchTaskAgent()` (line 161): `catch { return null; }`
- `McpHttpClient.RequestAsync()` (line 63): `catch { return null; }`
- `MainWindow.PollMessagesAsync()` (line 563): `catch { }`

**Fix:** At minimum, log the exception. For `McpHttpClient`, return a typed error result instead of null. For `PollMessagesAsync`, log to the UI log panel.

### M8. No Consistent Error Handling Strategy
**Rule violated:** design-robustcode §Error Handling Strategies ("Error handling is an ARCHITECTURE decision")

Current state:
- `LaunchService`: returns `Dictionary` with `["error"]` key
- `McpHttpClient.RequestAsync`: returns `null` on failure
- `PoolService.ValidateTasks`: returns `string?` (null = OK)
- HTTP endpoints: return `Results.Json(new { error = ... }, statusCode)` inline
- MCP tools: return JSON with `{ "error": "..." }` as string

**Fix:** Define a project-wide error strategy. Suggestion: use `Result<T>` pattern in Core/Services. Convert to appropriate format (HTTP status, MCP JSON) only at the transport layer.

### M9. Unblocking Logic Duplicated Between Endpoints and MCP Tools
**Rule violated:** design-methods §Why Create (duplication)

The "check which tasks are unblocked after completion" logic appears in:
- `ShikigamiMcpTools.UpdateTaskStatus()` (line 289-294)
- `PoolEndpoints /complete` (line 106-111)

Both contain the same loop: find pending tasks whose dependencies are all completed.

**Fix:** Move to `PoolService.GetUnblockedTasks(pool, completedTaskId)`.

### M10. FindClaude() Searches PATH on Every Call
**Rule violated:** design-parameters §Binding Time

`CliRunner.FindClaude()` iterates through `$PATH` directories looking for `claude`, `claude.cmd`, `claude.exe` on every invocation. This is a load-time value (doesn't change during execution).

**Fix:** Cache the result on first call (lazy static).

### M11. PoolService Uses Untyped Input
**Rule violated:** design-parameters §Type Safety, design-methods §Parameters

```csharp
public string? ValidateTasks(List<Dictionary<string, object>> tasksBatch)
public PoolRecord CreatePool(string poolId, List<Dictionary<string, object>> tasksBatch, ...)
```

The private helper `GetDependsOn` manually extracts `depends_on` from a dictionary and handles both `List<string>` and `JsonElement` cases.

**Fix:** Deserialize into a typed `TaskInput` record at the MCP/HTTP boundary. PoolService receives strongly typed input.

### M12. Law of Demeter Violations in Endpoint Code
**Rule violated:** design-classes §Coupling

Chains like:
```csharp
pool.Tasks.Values.Count(t => t.Status == "pending")  // PoolEndpoints, line 69
pool.Tasks.Values.All(t => t.Status is "completed" or "failed")  // PoolEndpoints, line 73
pool.Agents.Values.Count(a => a.Active)  // ShikigamiMcpTools, line 216
```

Appear in Server code, reaching deep into Core models.

**Fix:** Add query methods to `PoolRecord` or `PoolService`: `GetPendingCount()`, `AreAllTerminal()`, `GetActiveAgentCount()`.

---

## Critical to Fix

### C1. Hidden State Machine in MainWindow (7+ Booleans)
**Rule violated:** architecture-base §Multi-Bool State Machine Smell

```csharp
private bool _running;
private bool _waitingInput;
private bool _userStopped;
private bool _inputIsStop;
private bool _idle;
private bool _keepActive;
private bool _hordeWaiting;
```

Truth table of possible combinations: 2^7 = 128. Many are invalid:
- `_running = true` + `_idle = true` → impossible (but not prevented)
- `_waitingInput = true` + `_running = true` → impossible
- `_hordeWaiting = true` + `_running = true` → impossible
- `_userStopped = true` + `_idle = true` → meaningless

The compiler cannot prevent any of these. Bugs here are silent and state-dependent.

**Fix:** Replace with a single enum:
```csharp
enum RunnerState
{
    Starting,
    Working,
    WaitingInput,      // user question or stop correction
    Idle,              // AGENT_IDLE received
    HordeWaiting,      // waiting for blocked tasks
    Completing,        // countdown to close
    Completed,
    Aborted,
}
```

Use a state machine with explicit transitions. `_keepActive` is an orthogonal flag (can stay as bool).

### C2. MainWindow is a God Class (~800 LOC, 10+ responsibilities)
**Rule violated:** design-classes §Classes to Avoid (God class)

`MainWindow.xaml.cs` handles:
1. CLI lifecycle management (BeginCliPass, FinishCliPass, RunCliPassAsync)
2. Prompt mode flow (LaunchPassAsync, marker validation)
3. Horde mode flow (DispatchNextTaskAsync, RelaunchHordeTaskAsync, EvaluateHordeResult)
4. Message polling (PollMessagesAsync)
5. State transitions (EnterIdle, ExitIdle, CompleteWithCountdown)
6. User input handling (AskUser, AskUserAfterStop, SendInput)
7. UI event routing (HandleEvent, AppendLog, scroll, zoom)
8. Dot pulse animation
9. Lifecycle management (StartAsync, Shutdown)

Fan-out: McpHttpClient, CliRunner, PromptBuilder, ShikigamiContextMemory, DeepSpaceTheme, EmojiIcon, DispatcherTimer (4 instances), AppArgs. That's 10+ dependencies — well beyond the Miller's Rule limit of 7.

**Fix:** Extract into focused classes:
- `PromptModeController` — prompt-mode flow + marker validation
- `HordeModeController` — horde flow + task dispatch + marker validation
- `MessagePoller` — periodic polling + display
- `RunnerStateManager` — state machine (replaces 7 booleans)

MainWindow becomes a thin shell that delegates to these controllers.

### C3. Thread Safety of Queues (List inside ConcurrentDictionary)
**Rule violated:** design-robustcode §Core Principle ("must not corrupt the system")

```csharp
public ConcurrentDictionary<string, List<MessageRecord>> Queues { get; } = new();
```

`ConcurrentDictionary` protects the dictionary itself, but `List<MessageRecord>` is not thread-safe. Every access requires manual locking:
```csharp
var queue = state.Queues.GetOrAdd(recipientId, _ => new List<MessageRecord>());
lock (queue) queue.Add(msg);
```

If **any** access site forgets the lock, you get a race condition. This is the definition of a "semantic encapsulation violation" — callers must know the locking protocol.

**Risk:** Currently there are 10+ sites that access queues. Each one manually locks. Adding a new endpoint that forgets to lock = silent data corruption.

**Fix:** Either:
- (A) Encapsulate in `ShikigamiState` with `EnqueueMessage(id, msg)` / `DrainQueue(id)` that handle locking internally
- (B) Replace `List<MessageRecord>` with `ConcurrentQueue<MessageRecord>` (but drain-all pattern needs careful handling)

Option A is preferred — it also fixes M5 (encapsulation).

### C4. No Input Validation Barricade on HTTP Endpoints
**Rule violated:** design-robustcode §Barricades, §Input Validation

HTTP endpoints parse JSON with no validation:
```csharp
var data = await ctx.Request.ReadFromJsonAsync<JsonElement>();
var senderId = data.GetProperty("sender_id").GetString()!;  // throws if missing
```

If a client sends malformed JSON or omits a required field, `GetProperty()` throws `KeyNotFoundException` → 500 Internal Server Error with a stack trace.

Only `/agents/register` has field validation (`required.Where(f => !data.TryGetProperty(f, out _))`). All other endpoints trust input blindly.

**Risk:** Any malformed HTTP request crashes the endpoint. In production, this could be triggered by a bug in any Runner client, not just malicious input.

**Fix:** Add a validation barricade:
1. Create typed request DTOs for each endpoint
2. Validate at the boundary (missing fields, invalid values, length limits)
3. Return 400 with descriptive error messages
4. Internal code can then trust the data

---

## Priority Roadmap

| Phase | Items | Effort | Impact |
|---|---|---|---|
| 1. Safety | C3 (queue thread safety), C4 (input validation) | Small | Prevents data corruption and crashes |
| 2. Type Safety | C1 (state enum), M1 (status enums), M3 (timestamps) | Medium | Eliminates entire class of silent bugs |
| 3. Encapsulation | M5 (state encapsulation), M4 (drain pattern), M9 (unblock logic) | Medium | Reduces duplication, centralizes locking |
| 4. Architecture | C2 (split MainWindow), M2 (typed DTOs), M8 (error strategy) | Large | Enables safe growth of horde/prompt features |
| 5. Polish | M6 (shared colors), M7 (empty catches), M10 (cache claude path), M11 (typed pool input), M12 (Demeter) | Small | Code quality, maintainability |

Phase 1 should happen before any feature work. Phase 2-3 can be incremental. Phase 4 should happen before the next major feature addition to MainWindow.
