# Shikigami Project

**Version:** v2.0.0
**Platform:** .NET 9, C#, WPF (Windows only)
**Purpose:** MCP server + GUI runner for Claude Code sub-agents ("shikigami").

---

## What This Project Does

Shikigami is a management system for Claude Code sub-agents ("shikigami" — summoned spirits). It provides:

1. **MCP Server** — stdio JSON-RPC transport for Claude Code main chat + HTTP REST for shikigami communication
2. **Runner GUI** — WPF window per shikigami: persistent `claude` CLI session, live event stream, input panel
3. **Status Dashboard** — WPF window showing server stats, active shikigami, costs, pool progress

---

## Naming Convention

| Term | Meaning |
|---|---|
| Shikigami | A Claude Code sub-agent (replaces "subagent") |
| Runner | WPF GUI process managing one shikigami's CLI session |
| Lead | The parent Claude Code session that spawned shikigami |
| Pool | A set of tasks with dependencies (Horde mode) |
| Horde mode | Multiple shikigami executing pool tasks in parallel |
| Turn | One message → response cycle within a persistent CLI session |

---

## Solution Structure

```
ShikigamiProject.sln
│
├── src/
│   ├── Shikigami.Core/              — Class library (.NET 9)
│   │   ├── Models/                   — AgentRecord, PoolRecord, TaskRecord, MessageRecord, PromptRecord, MessageQueue
│   │   ├── State/                    — ShikigamiState (in-memory thread-safe store)
│   │   ├── Services/                 — IdGenerator, LaunchService, PidMonitor, PoolService
│   │   └── Shikigami.Core.csproj
│   │
│   ├── Shikigami.Server/            — Web app (.NET 9-windows; UseWPF + UseWindowsForms for tray)
│   │   ├── Mcp/ShikigamiMcpTools.cs  — All MCP tools exposed to Claude Code
│   │   ├── Http/                    — Minimal-API endpoint maps (AgentEndpoints, PoolEndpoints)
│   │   ├── Ui/                      — Status Dashboard (StatusWindow.xaml + Launcher + tray EmojiIcon)
│   │   ├── Program.cs               — Entry point: picks free port, wires MCP + HTTP + dashboard
│   │   ├── ServerSettings.cs
│   │   └── Shikigami.Server.csproj
│   │
│   └── Shikigami.Runner/            — WPF app (.NET 9-windows)
│       ├── MainWindow.xaml/.cs       — Single Window, implements IRunnerView directly
│       ├── App.xaml/.cs              — WPF app bootstrap
│       ├── Services/                 — CliSession, RunnerSession, McpHttpClient,
│       │                              PromptBuilder, RunResult, ShikigamiContextMemory, IRunnerView
│       ├── Theme/                    — DeepSpaceTheme palette + EmojiIcon helper
│       ├── Prompts/                  — Editable prompt templates (copied next to .exe as Prompts/)
│       └── Shikigami.Runner.csproj
│
├── Saved/ClaudeScratch/cli-events/  — Recorded stream-json fixtures (`*.jsonl`)
│                                       used to verify CLI event parsing (07_subagent.jsonl, etc.)
│
└── docs/
    └── cli-stream-events.md         — Reference: every CLI stream-json event and field
```

**Note:** Runner is intentionally MVVM-free — `MainWindow` directly implements `IRunnerView` and `RunnerSession` is the presenter. `Views/`/`ViewModels/` folders do not exist.

---

## Architecture Overview

```
Claude Code (main chat)
    │
    │ [MCP Protocol — stdio JSON-RPC]
    ▼
Shikigami.Server (long-running .exe — Kestrel + MCP stdio + WPF)
    ├── MCP tools (ShikigamiMcpTools) → create/list/message/cost/wait/...
    ├── HTTP REST (Kestrel) on 127.0.0.1, port picked at startup (random free)
    ├── PidMonitor (15s tick) → marks dead shikigami, notifies parents
    ├── Status Dashboard (StatusWindowLauncher) → WPF window on dedicated STA
    │                                              thread, tray icon when minimized
    └── ShikigamiState (in-memory, thread-safe ConcurrentDictionaries)
         │
         │ [HTTP REST — http://127.0.0.1:{port}]
         ▼
Shikigami.Runner (WPF process, one per shikigami)
    ├── CliSession — persistent `claude` CLI process per Runner
    │     Stream-json on stdin/stdout; context kept inside CLI harness
    │     Crash recovery via --resume <session-id>
    ├── RunnerSession — presenter; orchestrates turns, evaluates markers,
    │                    routes events into UI via IRunnerView
    ├── McpHttpClient — calls server's HTTP REST (register, state, messages, results)
    ├── MainWindow — IRunnerView impl: header, stats bar, scrollable log,
    │                 collapsible thinking blocks, sub-agent blocks, input panel
    └── Modes: Prompt mode (single task) OR Horde mode (sequential pool tasks)
```

---

## Persistent CLI Session (v2.0)

### How It Works

Runner launches ONE `claude` CLI process per shikigami and keeps it alive for the entire session. Messages are sent as NDJSON via stdin, responses are read as stream-json from stdout.

**Launch command:**
```
claude -p
    --input-format stream-json      # accept NDJSON on stdin
    --output-format stream-json     # emit NDJSON on stdout
    --verbose
    --strict-mcp-config
    --session-id <uuid>             # first launch; on resume: --resume <uuid>
    [--agent <agent>]               # if specified takes precedence over --model
    [--model <model>]
    [--allowedTools <csv>]
    [--effort <level>]              # reasoning effort (low/medium/high), resolved from agent YAML
```
Initial-launch env scrubbed: `CLAUDECODE`, `CLAUDE_CODE_SSE_PORT`, `CLAUDE_CODE_ENTRYPOINT`, `CLAUDE_CODE_MAX_OUTPUT_TOKENS` are removed before spawning to avoid interference from the parent Claude Code process.

**Input protocol (stdin):**
```json
{"type":"user","message":{"role":"user","content":"message text"}}
```
One JSON object per line. UTF-8 without BOM (`new UTF8Encoding(false)`).

**Output events (stdout):**

| Event | When |
|---|---|
| `system` subtype=`init` | Start of each turn (also re-emitted in persistent session) |
| `system` subtype=`task_started` | Sub-agent (Task/Agent tool) started — carries `tool_use_id`, `description`, `task_type`, `prompt` |
| `system` subtype=`task_progress` | Sub-agent progress update — carries `tool_use_id`, `description`, `last_tool_name` |
| `system` subtype=`task_notification` | Sub-agent finished — carries `tool_use_id`, `status`, `summary`, `usage.duration_ms` |
| `assistant` | Model response: `text`, `thinking`, `tool_use` blocks + usage stats |
| `user` | Tool results (`tool_result` blocks). Top-level `parent_tool_use_id` routes the event into a sub-agent or main log |
| `rate_limit_event` | Rate limit info (informational, ignored by Runner) |
| `result` | Turn complete — contains cost, final text, usage; reading this breaks the read loop |

⚠️ The parent `Task` tool is named `Agent` in the API — `ExtractToolDetail` recognizes it as `"Agent"`, not `"Task"`.

**Turn flow (with sub-agent):**
```
[send message via stdin]
  ← system/init
  ← assistant (Agent tool_use)            ← parent_tool_use_id=null
  ← system/task_started                   ← creates sub-agent block in UI
  ← user (text — sub-agent's input)        ← parent_tool_use_id=<task uuid>
  ← system/task_progress                  ← appends "… <desc> [<tool>]" to block
  ← assistant (sub-agent's tool_use)      ← parent_tool_use_id=<task uuid>
  ← user (sub-agent's tool_result)        ← parent_tool_use_id=<task uuid>
  ← system/task_notification              ← updates block header to ✓/✗ + status
  ← user (tool_result for parent's Agent) ← parent_tool_use_id=null
  ← assistant (text — main agent's reply)
  ← result/success
```

### Thinking blocks are encrypted (Opus 4.7+)

Since Claude Opus 4.7 / Claude Code v2.1.112, extended thinking content is **no longer exposed in plain text**. The block still arrives, but shaped like this:

```json
{"type": "thinking", "thinking": "", "signature": "<~4000 chars of encrypted payload>"}
```

| Field | Meaning |
|---|---|
| `thinking` | Always empty string — raw reasoning text is NOT accessible client-side |
| `signature` | Encrypted thinking, used only for multi-turn context continuity by the CLI/API |

**Consequences for the Runner:**

- `CliSession.cs` extracts `thinking` as before — the value is empty, so `RunnerSession.HandleCliEvent`'s `case "thinking"` falls through to the no-content branch:
  - sub-agent thinking → appended as `(thinking…)` line in the sub-agent block
  - main-agent thinking → `AppendLog("(thinking...)", "dim")` (no collapsible content to expand)
- When the model is instructed to reason, it often **duplicates the reasoning into a regular `text` block** that follows the empty thinking block. This text renders via `case "text"` as a normal response — NOT as collapsible. This is why reasoning now appears "unfolded" in the log compared to pre-4.7 behavior.
- Nothing to fix in our code — the reasoning text is intentionally withheld by Anthropic. Downgrading the CLI or changing flags does not bring the plain text back.

### Key Benefits vs Old One-Shot Model

| Metric | Old (relaunch per turn) | New (persistent) |
|---|---|---|
| System prompt cost | ~$0.18 per launch | $0.18 once, then $0.01/turn (cached) |
| Context | Manual JSON reconstruction | Maintained by CLI harness |
| Startup overhead | Full (MCP init, CLAUDE.md) | Zero after first message |
| Crash recovery | None | `--resume` restores full context |

### Crash Recovery

```
SendMessageAsync(...) called
  → EnsureCliAlive() — if !_cli.IsAlive:
       _cli.Restart(resume: true)        # --resume <session-id>
       AppendLog("[session] Resumed")
  → SendMessage(content, ...)            # next user/system message goes through
                                            the resumed session, full context restored
```

User-initiated stop (Stop button) follows the same pattern: `_cli.Kill()` → next user input triggers `_cli.Restart(resume:true)` and a corrective message:
- Prompt mode: `"User stopped you and instructed: <text>. Apply the correction and complete the task."`
- Horde mode: same shape; on no-marker the task is then failed via HTTP `tasks/{id}/fail`.

### Sub-agent block rendering (UI)

When the main agent invokes the `Agent` tool (Task), the Runner renders a single collapsible block per sub-agent invocation. The block accumulates everything the sub-agent does:

- **Created** on `system/task_started` — header shows `⌬ Sub-agent (<task_type>): <description>` in amber.
- **Body** is a vertical `StackPanel` of `TextBlock` lines (one per logical line, colored by tag). Filled by:
  - `prompt: …` from `task_started.prompt`
  - `… <description> [<last_tool_name>]` from each `task_progress`
  - `▶ <ToolName> <detail>` from sub-agent `assistant.tool_use` (matched via `parent_tool_use_id`)
  - `   ← <truncated content>` from sub-agent `tool_result` (truncated to 200 chars)
  - `(thinking…)` and `<text>` from sub-agent `thinking` / `text` blocks
- **Closed** on `system/task_notification` — header rewritten to `⌬ Sub-agent ✓ completed in <ms>ms: <summary>` (green) or `✗ <status>` (red).
- **Routing rule**: an event with non-empty `parent_tool_use_id` goes into the matching block; empty or null → main log. If the block is missing for any reason, content is surfaced in the main log as `[orphan sub-agent <id8>] …` so it isn't silently lost.

⚠️ The body is a `StackPanel` of `TextBlock`s, NOT a single mutated `TextBox`. Reason: `BlockUIContainer` does not always re-flow when a parented `TextBox.Text` is mutated, so dynamic appends were invisible. Adding new `TextBlock` children re-flows reliably.

### Limitations

- No graceful interrupt: Stop button kills the process, then restarts with `--resume`
- `--input-format stream-json` protocol is undocumented (verified by R&D fixtures in `Saved/ClaudeScratch/cli-events/`)
- CLI may hang after `result` event (bug #25629) — `CliSession` uses a `ReadLineTimeout` (10 hours) as a hard ceiling and kills the process if no output arrives within it

---

## Runner Services

### `CliSession.cs`
Persistent Claude CLI process wrapper.

**Constructor:** `CliSession(agent?, model?, tools?, workdir?, effort?, sessionId?)` — all optional; `sessionId` defaults to `Guid.NewGuid()`.

| Member | Kind | Purpose |
|---|---|---|
| `Start()` | method | Launch `claude` process (does NOT send a message — waits on stdin) |
| `SendMessage(content, onEvent)` | method | Send NDJSON, block until `result`. `onEvent(type, dict)` is invoked per parsed event |
| `Kill()` | method | Kill process tree via `taskkill /T /F /PID …` (5s wait); fallback to `Process.Kill(entireProcessTree:true)` |
| `Close()` | method | Close stdin gracefully (10s wait); falls back to `Kill()` if process hasn't exited |
| `Restart(resume)` | method | Kill + relaunch. `resume=true` → `--resume <sessionId>`; `false` → fresh `--session-id` |
| `IsAlive` | property | `true` if process exists and hasn't exited |
| `SessionId` | property | UUID used for `--session-id` / `--resume` |
| `LastStderr` | property | Captured stderr for crash diagnostics |

`onEvent` callback emits these synthetic types: `system`, `subagent_start`, `subagent_progress`, `subagent_end`, `tool`, `tool_result`, `text`, `thinking`, `usage`, `marked_result`, `result`, `error`. Each event-dict carries `parent_tool_use_id` when applicable — that's how the UI routes content into sub-agent blocks.

### `RunnerSession.cs`
Orchestrates shikigami lifecycle using `CliSession`.

**Prompt mode flow:**
```
StartAsync()
  → _cli.Start()
  → SendMessageAsync(initialPrompt)     # MCP header + comm + task
  → EvaluatePromptResult()

OnUserInput(text)
  → SendMessageAsync(text)              # raw text, context preserved
  → EvaluatePromptResult()

OnStopClicked()
  → _cli.Kill()
  → Show input panel

OnStopCorrection(text)
  → _cli.Restart(resume: true)
  → SendMessageAsync(correction)
```

**Horde mode flow:**
```
StartAsync()
  → _cli.Start()
  → DispatchNextTaskAsync()

DispatchNextTaskAsync() loop:
  → First task: SendMessageAsync(fullPrompt)    # MCP header + comm + task
  → Subsequent: SendMessageAsync(taskOnly)       # just task desc (rules known)
  → EvaluateHordeResult()
```

### `PromptBuilder.cs`
Builds the initial prompt only (first message in persistent session). Templates are loaded from `.txt` files next to the executable (`Prompts/`); built-in defaults are used when a file is missing.

**Constructor:** `PromptBuilder(originalPrompt, mcpPort?, promptId?, skipCommDirective=false, leadId="lead")`

| Member | Kind | Purpose |
|---|---|---|
| `BuildInitialPrompt()` | instance | MCP header + comm directive (unless `skipCommDirective`) + `## Your task:` + original prompt |
| `BuildTaskPrompt(title, description, mcpPort, agentId, poolId, leadId)` | static | Pool MCP header + horde comm directive (with `{title}`) + `## Task: <title>` + description |

Subsequent messages are raw text — no prompt rebuilding needed.

### `ShikigamiContextMemory.cs`
Pure audit log. Tracks events for UI display and server reporting.

| Method | Purpose |
|---|---|
| `FlushEvents()` | Record events from a CLI turn |
| `AddUserInput()` / `AddMessage()` / `AddUserStop()` | Track user interactions |
| `BeginTask()` | Mark horde task boundary |
| `Entries` | Read-only event list for debugging |

**Not used for prompt building** — CLI maintains context internally.

### `RunResult.cs`
Result of a single CLI turn. Contains: `ResultText`, `MarkedResult`, `ToolsUsed`, `Cost`, `Events`, `Error`, `ContextWindow`, `InputTokens`, `OutputTokens`.

---

## Key Concepts

### Prompt Mode
One shikigami = one task. First message contains full prompt (MCP header + communication directives + task). Subsequent messages are user input, corrections, or messages from other agents.

### Horde Mode (Pools)
A pool contains tasks with dependencies. Server launches one Runner per unique `agent_type`. Runner executes tasks sequentially in a persistent session — the shikigami retains knowledge from previous tasks. First task gets full prompt, subsequent tasks get just the task description.

### Communication
- **Lead → Shikigami:** Messages via MCP tools, delivered to Runner via HTTP polling
- **Shikigami → Lead:** Messages via HTTP POST, retrieved by lead via MCP `check_messages`
- **Shikigami ↔ Shikigami:** Via lead relay or pool broadcast

### Marker Protocol
Every shikigami response must end with a completion marker:

| Marker | Meaning |
|---|---|
| `USER_INPUT_REQUIRED: <question>` | Needs user input — shows input panel |
| `AGENT_IDLE` | Task done, stay alive for follow-up |
| `AGENT_COMPLETED` | Task done, close after countdown |
| `TASK_COMPLETED` | Horde task done |
| `TASK_FAILED: <reason>` | Horde task failed |

Result summary wrapped in `AGENT_RESULT_BEGIN` / `AGENT_RESULT_END`. Only the **main-agent** `text` blocks are scanned — `text` from sub-agents (`parent_tool_use_id != null`) is shown in the sub-agent block but never triggers `MarkedResult` extraction. This prevents a sub-agent that happens to write `AGENT_RESULT_END` from short-circuiting the parent's flow.

No marker → correction message sent in same session (prompt mode: up to 3 retries; horde mode: 1 retry, then task is failed).

---

## HTTP API (Shikigami → Server)

### Agent Endpoints
| Method | Path | Purpose |
|---|---|---|
| POST | `/agents/register` | Register a new shikigami |
| POST | `/agents/{id}/unregister` | Unregister (marks dead) |
| PUT | `/agents/{id}/state` | Update `current_step`; auto-notifies parent on terminal/idle/taken transition |
| GET | `/agents` | List active shikigami `[{id, name, agent_type}]` |
| GET | `/agents/{id}/state` | Get current state |
| GET | `/agents/{id}/result` | Get completed shikigami result |
| PUT | `/agents/{id}/result` | Submit `result` + `event_log` |
| PUT | `/agents/{id}/cost` | Submit cost (works for prompt agents AND pool agents — searches both) |
| GET | `/agents/{id}/wait?timeout=…` | Long-poll until terminal/idle/taken (default 1800s) |
| POST | `/agents/create` | HTTP mirror of MCP `create_agent_with_prompt` — requires `lead_id` |
| POST | `/messages/send` | Send message (`sender_id`, `recipient_id`, `text`); rejected → trash |
| GET | `/messages/{agent_id}` | Drain inbox (consumes; pushes to trash with reason `read`) |
| GET | `/messages/{agent_id}/wait?timeout=…` | Long-poll inbox (default 1800s) |
| GET | `/prompts/{prompt_id}` | Fetch stored prompt text |

### Pool Endpoints (Horde)
| Method | Path | Purpose |
|---|---|---|
| POST | `/pools/create` | Create pool + launch agents (requires `tasks`, `lead_id`) |
| GET | `/pools/{pool_id}/tasks` | List all tasks with statuses, deps, assignments |
| GET | `/pools/{pool_id}/tasks/{task_id}/result` | Get a single task's result |
| GET | `/pools/{pool_id}/tasks/request?agent_type=…&agent_id=…` | Request next task; returns `{task, remaining}` or `{task:null, all_done\|reason:"blocked", blocked_agent_types}` |
| PUT | `/pools/{pool_id}/tasks/{task_id}/complete` | Complete task; returns unblocked task IDs |
| PUT | `/pools/{pool_id}/tasks/{task_id}/fail` | Fail task; cascades to dependents |
| GET | `/pools/{pool_id}/wait?timeout=…` | Long-poll until pool finishes (default 1800s) |
| POST | `/pools/{pool_id}/agents/register` | Register horde agent (`agent_id`, `agent_type`, `pid`) |
| PUT | `/pools/{pool_id}/agents/{agent_id}/state` | Update agent state/detail |
| DELETE | `/pools/{pool_id}/agents/{agent_id}` | Unregister horde agent |
| POST | `/pools/{pool_id}/messages/send` | Pool message; `recipient_id="all"` broadcasts |
| GET | `/pools/{pool_id}/messages/check?agent_id=…` | Drain agent's pool inbox |

---

## MCP Tools (Claude Code → Server)

| Tool | Purpose |
|---|---|
| `get_http_port` | Return HTTP port for shikigami connections |
| `list_agents` | List active shikigami |
| `get_agent_state` | Get shikigami state by ID |
| `send_message` | Send message to a shikigami (sender is auto-`lead`) |
| `check_messages` | Drain lead inbox (instant) |
| `wait_for_messages` | Long-poll the lead inbox (default 1800s). Server-generated events arrive prefixed `[child_update]`, `[task_update]`, `[pool_update]` |
| `wait_for_agent` | Block until an agent is terminal/idle/taken/timeout |
| `wait_for_pool` | Block until a pool finishes |
| `get_agent_result` | Get completed shikigami result |
| `get_agent_log` | Get event log |
| `get_trash` | Debug: view message trash (last N) |
| `get_total_cost` | Cost breakdown across all agents (prompt + pool) |
| `create_agent_with_prompt` | One-shot create + launch (prompt mode) |
| `create_tasks` | Create pool + auto-launch (Horde) |
| `list_pools` | List all pools with task counts |
| `list_pool_tasks` | List tasks in pool |
| `get_pool_task_result` | Get the result of a single pool task |
| `abort_pool` | Abort a pool — running agents finish current task but get no new ones |
| `update_task_status` | Manual override: pending/completed/failed (with cascade/reopen logic) |
| `check_pool_messages` | Drain pool lead inbox |
| `send_pool_message` | Send message to a specific pool agent |
| `DEBUG_list_prompts` | Debug: list all stored prompts with full text |

---

## UI Theme

**Domain Expansion (領域展開)** aesthetic — Jujutsu Kaisen inspired. Dark occult void with cursed-energy violet, malevolent crimson, and infinity blue. Defined in `Theme/DeepSpaceTheme.cs` (class name kept for legacy reasons).

| Token | Hex | Role |
|---|---|---|
| `Bg` | `#08060F` | The Void — main background |
| `BgDark` | `#04030A` | Header gradient anchor |
| `BgSurface` | `#13101E` | Input panel surface |
| `BgPanel` | `#0D0A17` | Stats / button panels |
| `Fg` | `#B8C2D0` | Body text — silver |
| `FgDim` | `#4A3D65` | Dim labels — muted purple |
| `FgBright` | `#E4E8F0` | Headings |
| `Teal` | `#8B5CF6` | Cursed Energy (呪力) — primary accent (violet) |
| `TealDim` | `#2D1B69` | Dim teal/violet |
| `Cyan` | `#60A5FA` | Infinity Blue (無下限) — tools & techniques |
| `Amber` | `#F59E0B` | Cursed Flame (呪炎) — warnings, sub-agent header |
| `AmberDim` | `#2D1F05` | Dim amber background |
| `Green` | `#34D399` | Reverse Cursed Technique (反転術式) — success |
| `GreenDim` | `#0A2E1F` | Dim green background |
| `Red` | `#EF4444` | Malevolent (宿儺) — danger |
| `Lavender` | `#A78BFA` | Header agent name |
| `Peach` | `#D4A574` | Special accent |

Fonts: `FontUi = "Yu Gothic UI"` (kanji-friendly), `FontMono = "Consolas"` (log).
Brushes are pre-frozen (`Freeze()`) for performance.

⚠️ The class is still named `DeepSpaceTheme` — renaming was deferred to avoid ripple changes across XAML/code-behind. The palette inside is fully Domain-Expansion.

---

## Installation

```
~/.claude/MCPs/ShikigamiMCP/
├── Server/
│   └── Shikigami.Server.exe    ← MCP server (registered in Claude CLI)
└── Runner/
    ├── Shikigami.Runner.exe    ← WPF GUI (launched by Server per shikigami)
    └── Prompts/                ← Editable prompt templates
```

Server finds Runner via relative path: `../Runner/Shikigami.Runner.exe`.

```bash
build-shipping.bat        # Compile Release to Build/Shipping/
build-debug.bat           # Compile Debug to Build/Debug/
install.bat               # robocopy Server + Runner to ~/.claude/MCPs/ShikigamiMCP/
"Install Only Runner.bat" # robocopy ONLY Runner — useful when iterating on UI without
                          # restarting the running Server (which would drop MCP connection)
```

Manual MCP registration:
```bash
claude mcp add ShikigamiMCP -- "%USERPROFILE%\.claude\MCPs\ShikigamiMCP\Server\Shikigami.Server.exe"
```

---

## Development

### Prerequisites
- .NET 9 SDK
- Windows 10/11 (WPF requirement)
- `claude` CLI in PATH

### Build
```bash
dotnet build                # quick dev build
build-shipping.bat          # Release → Build/Shipping/
build-debug.bat             # Debug → Build/Debug/
```

### Test

There is no automated test project at the moment (the `tests/` folder exists but is empty).

### Stream-JSON Fixtures
Real CLI output captured to `Saved/ClaudeScratch/cli-events/*.jsonl` is the reference for parsing. Notable files:
- `01_simple.jsonl` — basic turn (init + assistant text + result)
- `02_thinking.jsonl` — encrypted thinking block (post-Opus 4.7)
- `03_tools.jsonl` — tool_use + tool_result + cyrillic
- `06_multiturn.jsonl` — two-turn replay with `isReplay:true` user echo
- `07_subagent.jsonl` — Task→Agent sub-agent invocation (`task_started/progress/notification`)
- `08_parallel.jsonl` — parallel `tool_use` blocks in one assistant message

See `docs/cli-stream-events.md` for the full field reference.

---

## Prompt Templates

Runner loads prompt templates from `.txt` files in `Prompts/` next to its executable. If a file is missing, a built-in default is used. Only used for the **first message** in a persistent session.

| File | Purpose | Placeholders |
|---|---|---|
| `prompt_comm.txt` | Communication directive (prompt mode) | — |
| `prompt_horde_comm.txt` | Communication directive (horde mode) | `{title}` |
| `prompt_mcp_header.txt` | MCP connection header (prompt mode) | `{port}`, `{agent_id}`, `{lead_id}` |
| `prompt_pool_mcp_header.txt` | MCP connection header (horde mode) | `{port}`, `{agent_id}`, `{lead_id}`, `{pool_id}` |

---

## TODO

| Feature | Details |
|---|---|
| Horde idle/backoff | `DispatcherTimer` poll (5s) implemented — needs more real-world testing |
| Prompt editor button | UI button to open prompt template files in external editor |
| Tighter CLI hang ceiling | `ReadLineTimeout` is currently 10 hours (very generous); consider per-tool granularity |
| Session cleanup | Clean up saved session files older than 24h |
| Automated tests | `tests/` folder is empty — at minimum, parse-tests over `Saved/ClaudeScratch/cli-events/*.jsonl` |
