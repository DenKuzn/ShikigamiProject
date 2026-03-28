# Shikigami Project

**Version:** v1.0.0
**Platform:** .NET 9, C#, WPF (Windows only)
**Purpose:** Replace the Python-based SubagentMCP server and StartSubagent runner with a native .NET application.

---

## What This Project Does

Shikigami is a management system for Claude Code sub-agents ("shikigami" — summoned spirits). It provides:

1. **MCP Server** — stdio JSON-RPC transport for Claude Code main chat + HTTP REST for shikigami communication
2. **Runner GUI** — WPF window per shikigami: launches `claude` CLI, parses stream-json, shows live status
3. **Status Dashboard** — WPF window showing server stats, active shikigami, costs, pool progress

This replaces:
- `~/.claude/MCPs/SubagentMCP/` (Python MCP server with Tkinter status window)
- `~/.claude/scripts/StartSubagent.py` + `~/.claude/scripts/subagent/` (Python GUI runner)

---

## Naming Convention

All "subagent" terminology is replaced with "shikigami" in this project.

| Old (Python) | New (Shikigami) |
|---|---|
| SubagentMCP | ShikigamiMCP |
| subagent | shikigami |
| StartSubagent.py | Shikigami.Runner |
| Horde mode | Horde mode (unchanged) |
| Pool | Pool (unchanged) |
| Lead | Lead (unchanged) |

**Exception:** External interfaces (MCP tool names, HTTP API paths) may retain compatibility names where needed for Claude Code integration. Document any such exceptions here.

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
│       ├── Services/                — CLI runner, MCP HTTP client, prompt builder, context memory
│       ├── Theme/                   — Deep Space color palette, styles
│       ├── Prompts/                 — Editable prompt templates (copied to output as Prompts/)
│       └── Shikigami.Runner.csproj
│
└── tests/
    └── Shikigami.Core.Tests/        — Unit tests (xUnit)
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
    └── Status dashboard → WPF window in separate thread (🐇 tray icon when minimized)
         │
         │ [HTTP REST — localhost:{port}]
         ▼
Shikigami.Runner (WPF process, one per shikigami)
    ├── Launches `claude` CLI as subprocess with stream-json output
    ├── Parses events in real-time (tools, thinking, text, result)
    ├── Registers with server, sends state updates, receives messages
    ├── Supports Prompt mode (single task) and Horde mode (pool task loop)
    └── GUI: header, stats bar, scrollable log, input panel
```

---

## Key Concepts

### Prompt Mode
One shikigami = one prompt. Server stores the prompt, launches Runner, Runner fetches prompt, executes, submits result.

### Horde Mode (Pools)
A pool contains tasks with dependencies. Server launches one Runner per unique `agent_type`. Each Runner polls for available tasks, executes them sequentially, reports completion/failure. Tasks auto-unblock when dependencies complete. Failed tasks cascade to dependents.

### Communication
- **Lead → Shikigami:** Messages via MCP tools, delivered to Runner via HTTP polling
- **Shikigami → Lead:** Messages via HTTP POST, retrieved by lead via MCP `check_messages`
- **Shikigami ↔ Shikigami:** Via lead relay or pool broadcast

---

## HTTP API (Shikigami → Server)

Preserved from the Python original. Must remain compatible:

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
| PUT | `/agents/{id}/result` | Submit result + event log |
| PUT | `/agents/{id}/cost` | Submit cost |
| GET | `/prompts/{prompt_id}` | Fetch stored prompt |
| GET | `/agents/{id}/wait` | Long-poll until complete |
| POST | `/agents/create` | Create + launch (HTTP mirror) |

### Pool Endpoints (Horde)
| Method | Path | Purpose |
|---|---|---|
| POST | `/pools/create` | Create pool + launch agents |
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

## Installation Layout

```
~/.claude/MCPs/ShikigamiMCP/
├── Server/
│   └── Shikigami.Server.exe    ← MCP server (registered in Claude CLI)
└── Runner/
    ├── Shikigami.Runner.exe    ← WPF GUI (launched by Server per shikigami)
    └── Prompts/                ← Editable prompt templates
```

Server finds Runner via relative path: `../Runner/Shikigami.Runner.exe` from its own directory.

### Install
```bash
build-shipping.bat   # compile Release to Build/Shipping/
install.bat          # copy to ~/.claude/MCPs/ShikigamiMCP/ + register in Claude CLI
```

### Registration command (manual)
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

### Run Server (dev)
```bash
dotnet run --project src/Shikigami.Server
```

### Run Runner (manual test)
```bash
dotnet run --project src/Shikigami.Runner -- --prompt-id <id> --mcp-port <port> --agent <name> --workdir <path>
```

### Test
```bash
dotnet test
```

---

## Prompt Templates

Runner loads prompt templates from `.txt` files next to its executable. If a file is missing, a built-in default is used. Placeholders are substituted at runtime.

| File | Purpose | Placeholders |
|---|---|---|
| `prompt_comm.txt` | Communication directive (prompt mode) | — |
| `prompt_horde_comm.txt` | Communication directive (horde mode) | `{title}` |
| `prompt_mcp_header.txt` | MCP connection header (prompt mode) | `{port}`, `{agent_id}`, `{lead_id}` |
| `prompt_pool_mcp_header.txt` | MCP connection header (horde mode) | `{port}`, `{agent_id}`, `{lead_id}`, `{pool_id}` |

Edit these files in `~/.claude/MCPs/ShikigamiMCP/Runner/Prompts/` to customize prompts without recompilation.

---

## Implementation Status

### Done
| Feature | Details |
|---|---|
| Solution structure | 3 projects: Core, Server, Runner |
| Models & State | AgentRecord, PoolRecord, TaskRecord, MessageRecord, PromptRecord, thread-safe ShikigamiState |
| MCP Server | 18 MCP tools via stdio JSON-RPC (ModelContextProtocol SDK) |
| HTTP REST API | 21 endpoints (agent + pool), full compatibility with Python original |
| PID Monitor | Background task, checks every 15s, marks dead agents |
| Pool Management | Validate, create, get available task, cascade failure, reopen dependents, completion check |
| Launch Service | Starts Runner process for prompt-mode and horde-mode agents |
| Runner CLI | Launches `claude` CLI, parses stream-json events (system, tool, thinking, text), UTF-8 encoding |
| Runner GUI | Deep Space theme, header with dot pulse, stats bar, scrollable log, input panel |
| Status Dashboard | WPF window with live stats (agents, prompts, msgs, results, logs, trash), cost banner, pool section |
| Tray Icon | 🐇 system tray when dashboard is closed, double-click to restore |
| Runner Window Icon | 🐇 icon on Runner window title bar (pure WPF rendering, no WinForms) |
| PromptBuilder | MCP header + communication directives, external template files, prompt/horde modes, full history JSON on iteration 2+ |
| Horde Mode | Task polling, dispatch, complete/fail reporting, pool lifecycle, TASK_COMPLETED/TASK_FAILED marker validation |
| Font Zoom | Ctrl + mouse wheel in Runner log area (6–30px) |
| Build Scripts | `build-shipping.bat` (Release), `build-debug.bat` (Debug), no .pdb in Release |
| Install Script | `install.bat` — robocopy to `~/.claude/MCPs/ShikigamiMCP/`, manual MCP registration hint |
| USER_INPUT_REQUIRED | Detects marker in CLI result, shows input panel, amber dot pulse, re-launches CLI with answer |
| Iteration loop | Re-launches CLI when message arrives (from any agent) while not running; context preserved between iterations |
| Smart auto-scroll | Terminal-like: scrolling up freezes position, returning to bottom resumes auto-scroll |
| Multiline input | Input panel supports Ctrl+Enter for newlines, Enter to send |
| Unicode-safe messaging | Prompt templates use `printf \| curl -d @-` pipe pattern for Cyrillic-safe HTTP messaging |
| Text wrapping | Log area wraps long lines by word instead of extending horizontally |
| Stop button | Kill CLI mid-execution, show input panel for correction, re-launch with `user_stop` context; horde-aware (uses `RelaunchHordeTaskAsync`) |
| Idle mode | AGENT_IDLE marker: Runner stays alive with green dot pulse, input panel open, accepts messages or user input |
| Keep Active button | Header toggle: prevents auto-close on COMPLETED, transitions to idle instead; cancels close timer if already counting |
| Auto-close on complete | AGENT_COMPLETED triggers 10s countdown in header (`closing in 10s...9s...`), then window closes; message polling paused during countdown |
| Marker validation (prompt) | Checks USER_INPUT_REQUIRED → AGENT_IDLE → AGENT_COMPLETED in order; no marker = re-launch for correction |
| Marker validation (horde) | Checks TASK_FAILED → TASK_COMPLETED; no marker = 1 retry then fail; horde-specific Stop/Message dispatch via `RelaunchHordeTaskAsync` |
| Horde waiting | DispatcherTimer poll (5s), distinguishes blocked/done/aborted, green dot + amber header with blocked count |
| Prompts folder | Prompt templates moved to `Prompts/` subdirectory next to exe |
| ShikigamiContextMemory | Filtered history (thinking, tool calls, text, user input, messages) accumulated across CLI passes, serialized as JSON into prompt for continuation |

### TODO
| Feature | Details |
|---|---|
| Horde idle/backoff | DispatcherTimer poll (5s) done, blocked/done/aborted distinction done — needs further testing and fixes |
| Prompt editor button | UI button to open prompt template files in external editor |

---

## Original Source Reference

The Python originals live at:
- Server: `~/.claude/MCPs/SubagentMCP/` (server.py, mcp_tools.py, http_handlers.py, state.py, ui.py)
- Runner: `~/.claude/scripts/StartSubagent.py` + `~/.claude/scripts/subagent/` (app.py, cli_runner.py, mcp_client.py, context.py, prompt_builder.py, theme.py)

These are the source of truth for behavior. When in doubt about how something should work, read the Python originals.
