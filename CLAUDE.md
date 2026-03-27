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
│   │   ├── Program.cs               — Entry point: wires MCP + HTTP + dashboard
│   │   └── Shikigami.Server.csproj
│   │
│   └── Shikigami.Runner/            — WPF app (.NET 9-windows)
│       ├── Views/                   — XAML windows
│       ├── ViewModels/              — MVVM view models
│       ├── Services/                — CLI runner, MCP HTTP client, prompt builder
│       ├── Theme/                   — Deep Space color palette, styles
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
    └── Status dashboard → WPF window in separate thread
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

## Development

### Prerequisites
- .NET 9 SDK
- Windows 10/11 (WPF requirement)
- `claude` CLI in PATH

### Build
```bash
dotnet build
```

### Run Server
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

## Original Source Reference

The Python originals live at:
- Server: `~/.claude/MCPs/SubagentMCP/` (server.py, mcp_tools.py, http_handlers.py, state.py, ui.py)
- Runner: `~/.claude/scripts/StartSubagent.py` + `~/.claude/scripts/subagent/` (app.py, cli_runner.py, mcp_client.py, context.py, prompt_builder.py, theme.py)

These are the source of truth for behavior. When in doubt about how something should work, read the Python originals.
