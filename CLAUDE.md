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
│   │   ├── Models/                   — Agent, Pool, Task, Message, Prompt records
│   │   ├── State/                    — In-memory state store (thread-safe)
│   │   ├── Services/                 — Business logic (PID monitor, pool management, cascade)
│   │   └── Shikigami.Core.csproj
│   │
│   ├── Shikigami.Server/            — Console app (.NET 9)
│   │   ├── Mcp/                     — MCP stdio transport + tool definitions
│   │   ├── Http/                    — REST API controllers for shikigami
│   │   ├── Ui/                      — Status Dashboard (WPF window + tray icon)
│   │   ├── Program.cs               — Entry point: wires MCP + HTTP + dashboard
│   │   └── Shikigami.Server.csproj
│   │
│   └── Shikigami.Runner/            — WPF app (.NET 9-windows)
│       ├── Views/                   — XAML windows
│       ├── ViewModels/              — MVVM view models
│       ├── Services/                — CliSession, RunnerSession, McpHttpClient, PromptBuilder
│       ├── Theme/                   — Deep Space color palette, styles
│       ├── Prompts/                 — Editable prompt templates (copied to output as Prompts/)
│       └── Shikigami.Runner.csproj
│
├── tests/
│   └── Shikigami.Core.Tests/        — Unit tests (xUnit)
│
└── docs/
    └── persistent-cli-migration.md  — R&D document for CLI session migration
```

---

## Architecture Overview

```
Claude Code (main chat)
    │
    │ [MCP Protocol — stdio JSON-RPC]
    ▼
Shikigami.Server (console process)
    ├── MCP tools → create/list/message/cost shikigami
    ├── HTTP REST server (localhost, dynamic port) → shikigami registration, state, messaging
    ├── PID monitor → detects dead shikigami every 15s
    └── Status dashboard → WPF window in separate thread (tray icon when minimized)
         │
         │ [HTTP REST — localhost:{port}]
         ▼
Shikigami.Runner (WPF process, one per shikigami)
    ├── Persistent `claude` CLI session (--input-format stream-json)
    ├── Sends messages via stdin NDJSON, reads responses from stdout
    ├── Context maintained by CLI harness internally (no manual history rebuild)
    ├── Crash recovery via --resume <session-id>
    ├── Registers with server, sends state updates, receives messages
    ├── Supports Prompt mode (single task) and Horde mode (pool task loop)
    └── GUI: header, stats bar, scrollable log, input panel
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
    --session-id <uuid>             # for crash recovery via --resume
    --strict-mcp-config
    [--model <model>]
    [--agent <agent>]
```

**Input protocol (stdin):**
```json
{"type":"user","message":{"role":"user","content":"message text"}}
```
One JSON object per line. UTF-8 without BOM (`new UTF8Encoding(false)`).

**Output events (stdout):**

| Event | When |
|---|---|
| `system` (subtype=init) | Start of each turn |
| `assistant` | Model response: text, thinking, tool_use blocks + usage stats |
| `user` | Tool results (internal, CLI handles tool execution) |
| `rate_limit_event` | Rate limit info (informational) |
| `result` | Turn complete — contains cost, final text, usage |

**Turn flow:**
```
[send message via stdin]
  ← system/init
  ← assistant (thinking, tool_use, text)
  ← user (tool_results)
  ← assistant (more text)
  ← result/success
  (process waits for next message)
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

- `CliSession.cs` extracts `thinking` as before — the value is empty, so `RunnerSession.HandleCliEvent` falls through to the `AppendLog("(thinking...)", "dim")` branch (no collapsible content to show).
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
Process dies unexpectedly
  → Restart with --resume <session-id>
  → Send: "You were interrupted. Continue."
  → Full context restored from saved session
  
Resume also fails?
  → Restart fresh with new --session-id
  → Send full initial prompt (first message)
```

### Limitations

- No graceful interrupt: Stop button kills the process, then restarts with `--resume`
- `--input-format stream-json` protocol is undocumented (verified by R&D tests)
- CLI may hang after result event (known bug #25629) — monitor needed

---

## Runner Services

### `CliSession.cs`
Persistent Claude CLI process wrapper.

| Method | Purpose |
|---|---|
| `Start()` | Launch `claude` process (waits for first stdin message) |
| `SendMessage(content, onEvent)` | Send NDJSON message, block until `result` event |
| `Kill()` | Kill process tree (`taskkill /T /F`) |
| `Close()` | Close stdin gracefully, wait for exit |
| `Restart(resume)` | Kill + relaunch. `resume=true` → `--resume <sessionId>` |
| `IsAlive` | Check if process is running |
| `SessionId` | UUID used for `--session-id` / `--resume` |
| `LastStderr` | Captured stderr for crash diagnostics |

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
Builds the initial prompt only (first message in persistent session).

| Method | Purpose |
|---|---|
| `BuildInitialPrompt()` | MCP header + comm directive + task (prompt mode) |
| `BuildTaskPrompt()` (static) | MCP header + comm directive + task (horde mode, first task) |

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

Result summary wrapped in `AGENT_RESULT_BEGIN` / `AGENT_RESULT_END`.

No marker → correction message sent in same session (up to 3 retries).

---

## HTTP API (Shikigami → Server)

### Agent Endpoints
| Method | Path | Purpose |
|---|---|---|
| POST | `/agents/register` | Register a new shikigami |
| POST | `/agents/{id}/unregister` | Unregister |
| PUT | `/agents/{id}/state` | Update status/step |
| GET | `/agents` | List active shikigami |
| POST | `/messages/send` | Send message |
| GET | `/messages/{agent_id}` | Poll inbox (consumes) |
| GET | `/agents/{id}/state` | Get shikigami state |
| GET | `/agents/{id}/result` | Get completed shikigami result |
| PUT | `/agents/{id}/result` | Submit result + event log |
| PUT | `/agents/{id}/cost` | Submit cost |
| GET | `/prompts/{prompt_id}` | Fetch stored prompt |
| GET | `/agents/{id}/wait` | Long-poll until complete |
| POST | `/agents/create` | Create + launch (HTTP mirror) |

### Pool Endpoints (Horde)
| Method | Path | Purpose |
|---|---|---|
| POST | `/pools/create` | Create pool + launch agents |
| GET | `/pools/{pool_id}/tasks` | List all tasks with statuses |
| GET | `/pools/{pool_id}/tasks/request` | Request next task |
| PUT | `/pools/{pool_id}/tasks/{task_id}/complete` | Complete task |
| PUT | `/pools/{pool_id}/tasks/{task_id}/fail` | Fail task |
| POST | `/pools/{pool_id}/agents/register` | Register horde agent |
| PUT | `/pools/{pool_id}/agents/{agent_id}/state` | Update horde agent state |
| DELETE | `/pools/{pool_id}/agents/{agent_id}` | Unregister horde agent |
| POST | `/pools/{pool_id}/messages/send` | Pool message |
| GET | `/pools/{pool_id}/messages/check` | Poll pool inbox |

---

## MCP Tools (Claude Code → Server)

| Tool | Purpose |
|---|---|
| `get_http_port` | Return HTTP port for shikigami connections |
| `list_agents` | List active shikigami |
| `get_agent_state` | Get shikigami state by ID |
| `send_message` | Send message to shikigami |
| `check_messages` | Drain lead inbox |
| `list_prompts` | List stored prompts |
| `get_agent_result` | Get completed shikigami result |
| `get_agent_log` | Get event log |
| `get_trash` | Debug: view message trash |
| `get_total_cost` | Cost breakdown |
| `create_agent_with_prompt` | One-shot create + launch |
| `create_tasks` | Create pool + auto-launch (Horde) |
| `list_pools` | List all pools |
| `list_pool_tasks` | List tasks in pool |
| `abort_pool` | Abort a pool |
| `update_task_status` | Manual task status override |
| `check_pool_messages` | Drain pool lead inbox |
| `send_pool_message` | Message agent in pool |

---

## UI Theme

**Deep Space** aesthetic (dark background, teal accents).

| Token | Hex |
|---|---|
| BG | `#0b0e17` |
| BG_DARK | `#060a10` |
| BG_SURFACE | `#161c2e` |
| BG_PANEL | `#0f1420` |
| FG | `#b8c5d6` |
| FG_DIM | `#4a5a6e` |
| FG_BRIGHT | `#e4eaf4` |
| TEAL | `#00e5c0` |
| TEAL_DIM | `#005c4d` |
| CYAN | `#5ec4ff` |
| AMBER | `#e5a000` |
| GREEN | `#7dff7d` |
| RED | `#ff5c5c` |
| LAVENDER | (Runner only) |
| PEACH | (Runner only) |

Fonts: `Bahnschrift` (UI), `Consolas` (monospace).

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
build-shipping.bat   # compile Release to Build/Shipping/
install.bat          # robocopy to ~/.claude/MCPs/ShikigamiMCP/ + register hint
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
```bash
dotnet test
```

### Stream-JSON R&D Tests
PowerShell scripts in `tests/` verify the persistent CLI protocol:
- `test-stream-simple.ps1` — basic persistent process test
- `test-stream-auth.ps1` — context retention + tool use + cyrillic
- `test-stream-tools.ps1` — tool use + cyrillic (UTF-8 fix)
- `test-stream-edge.ps1` — `--model`, `--session-id`, `--resume`, sequential messages

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
| Horde idle/backoff | DispatcherTimer poll (5s) done — needs further testing |
| Prompt editor button | UI button to open prompt template files in external editor |
| Safety timeout | Monitor for CLI hang after result event (bug #25629) |
| Session cleanup | Clean up saved session files older than 24h |
