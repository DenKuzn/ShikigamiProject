# Shikigami

**Sub-agent management system for [Claude Code](https://docs.anthropic.com/en/docs/claude-code).**

Shikigami lets Claude Code spawn, monitor, and orchestrate autonomous sub-agents ("shikigami") through MCP. Each sub-agent runs as a separate `claude` CLI process with its own GUI window, communicating with the lead conversation via an MCP server.

Built with .NET 9, C#, and WPF. Windows only.

---

## What It Does

- **MCP Server** -- stdio JSON-RPC transport for Claude Code + HTTP REST for sub-agent communication
- **Runner GUI** -- a WPF window per sub-agent: launches `claude` CLI, parses stream-json output, shows live status and logs
- **Status Dashboard** -- real-time overview of all active agents, costs, pools, and messages

### How It Works

```
Claude Code (main chat)
    |
    | MCP Protocol (stdio JSON-RPC)
    v
Shikigami.Server
    |-- MCP tools: create / list / message / monitor agents
    |-- HTTP REST server (localhost, dynamic port)
    |-- PID monitor (detects dead agents every 15s)
    |-- Status Dashboard (WPF, tray icon)
    |
    | HTTP REST (localhost:{port})
    v
Shikigami.Runner (one per agent)
    |-- Launches `claude` CLI as subprocess
    |-- Parses events in real-time (tools, thinking, text, result)
    |-- Registers with server, sends state updates, receives messages
    |-- GUI: header, stats bar, scrollable log, input panel
```

---

## Features

### Agent Modes

| Mode | Description |
|------|-------------|
| **Prompt** | One agent = one task. Server stores the prompt, launches Runner, Runner executes and submits result |
| **Horde** (Pools) | A pool of tasks with dependencies. Server launches one Runner per agent type. Each polls for available tasks, executes sequentially, reports completion/failure. Tasks auto-unblock when dependencies complete |

### Communication

- **Lead -> Agent:** messages via MCP tools, delivered to Runner via HTTP polling
- **Agent -> Lead:** messages via HTTP POST, retrieved by lead via MCP `check_messages`
- **Agent <-> Agent:** via lead relay or pool broadcast

### Runner GUI

- Deep Space dark theme (teal accents, `#0b0e17` background)
- Live streaming log with syntax-highlighted events
- Header with animated dot pulse showing agent state (running / idle / waiting)
- Stats bar (tokens, cost, duration)
- Font zoom (Ctrl + mouse wheel)
- Smart auto-scroll (terminal-like: scroll up freezes, return to bottom resumes)
- Multiline input (Ctrl+Enter for newlines)
- Stop button to kill CLI mid-execution and correct course
- Keep Active button to prevent auto-close on completion
- `USER_INPUT_REQUIRED` detection: shows input panel, re-launches CLI with answer
- `AGENT_IDLE` mode: agent stays alive, accepts messages or user input
- Auto-close with 10s countdown on completion

### Status Dashboard

- Live stats: active agents, prompts, messages, results, logs
- Cost banner with per-agent breakdown
- Pool section with task progress
- System tray icon when minimized

### Horde Mode (Pools)

- Task dependency graph with automatic unblocking
- Cascade failure propagation to dependent tasks
- Per-agent-type task dispatch
- Pool-level messaging and broadcast
- Abort pool command

---

## MCP Tools

These tools are exposed to the Claude Code main chat:

| Tool | Purpose |
|------|---------|
| `create_agent_with_prompt` | Create and launch a single agent with a prompt |
| `create_tasks` | Create a task pool and auto-launch agents (Horde mode) |
| `list_agents` | List all active agents |
| `get_agent_state` | Get agent status, current step, metadata |
| `get_agent_result` | Get completed agent result |
| `get_agent_log` | Get agent event log |
| `send_message` | Send message to an agent |
| `check_messages` | Drain lead inbox |
| `list_pools` | List all pools |
| `list_pool_tasks` | List tasks in a pool |
| `send_pool_message` | Message an agent in a pool |
| `check_pool_messages` | Drain pool lead inbox |
| `abort_pool` | Abort a running pool |
| `update_task_status` | Manual task status override |
| `get_http_port` | Get HTTP port for agent connections |
| `list_prompts` | List stored prompts |
| `get_total_cost` | Cost breakdown across all agents |
| `get_trash` | Debug: view message trash |

---

## Installation

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Windows 10/11
- [Claude Code CLI](https://docs.anthropic.com/en/docs/claude-code) (`claude` in PATH)

### Build & Install

```bash
# 1. Build Release
build-shipping.bat

# 2. Install to ~/.claude/MCPs/ShikigamiMCP/
install.bat

# 3. Register MCP server in Claude Code (first time only)
claude mcp add ShikigamiMCP -- "%USERPROFILE%\.claude\MCPs\ShikigamiMCP\Server\Shikigami.Server.exe"
```

After registration, restart Claude Code. The MCP tools will be available in your conversations.

### Installation Layout

```
~/.claude/MCPs/ShikigamiMCP/
  Server/
    Shikigami.Server.exe    <-- MCP server (registered in Claude CLI)
  Runner/
    Shikigami.Runner.exe    <-- WPF GUI (launched by Server per agent)
    Prompts/                <-- Editable prompt templates
```

---

## Development

### Build

```bash
dotnet build                # Quick dev build
build-shipping.bat          # Release -> Build/Shipping/
build-debug.bat             # Debug -> Build/Debug/
```

### Run Server (dev)

```bash
dotnet run --project src/Shikigami.Server
```

### Run Runner (manual test)

```bash
dotnet run --project src/Shikigami.Runner -- --prompt-id <id> --mcp-port <port> --agent <name> --workdir <path>
```

### Tests

```bash
dotnet test
```

### Project Structure

```
ShikigamiProject.sln
  src/
    Shikigami.Core/        -- Models, state store, services (class library)
    Shikigami.Server/      -- MCP server + HTTP API + Status Dashboard
    Shikigami.Runner/      -- WPF GUI per agent (CLI runner, log viewer)
  tests/
    Shikigami.Core.Tests/  -- Unit tests (xUnit)
```

---

## Prompt Templates

Runner loads prompt templates from `.txt` files in the `Prompts/` directory next to the executable. Edit these to customize agent behavior without recompilation:

| File | Purpose |
|------|---------|
| `prompt_comm.txt` | Communication directives (prompt mode) |
| `prompt_horde_comm.txt` | Communication directives (horde mode) |
| `prompt_mcp_header.txt` | MCP connection header (prompt mode) |
| `prompt_pool_mcp_header.txt` | MCP connection header (horde mode) |

Placeholders like `{port}`, `{agent_id}`, `{lead_id}`, `{pool_id}`, `{title}` are substituted at runtime.

---

## HTTP API

The server exposes a REST API on localhost for Runner <-> Server communication. Full endpoint list:

<details>
<summary>Agent Endpoints</summary>

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/agents/register` | Register a new agent |
| POST | `/agents/{id}/unregister` | Unregister |
| PUT | `/agents/{id}/state` | Update status/step |
| GET | `/agents` | List active agents |
| POST | `/messages/send` | Send message |
| GET | `/messages/{agent_id}` | Poll inbox (consumes) |
| GET | `/agents/{id}/state` | Get agent state |
| GET | `/agents/{id}/result` | Get completed result |
| PUT | `/agents/{id}/result` | Submit result + event log |
| PUT | `/agents/{id}/cost` | Submit cost |
| GET | `/prompts/{prompt_id}` | Fetch stored prompt |
| GET | `/agents/{id}/wait` | Long-poll until complete |
| POST | `/agents/create` | Create + launch (HTTP mirror) |

</details>

<details>
<summary>Pool Endpoints (Horde)</summary>

| Method | Path | Purpose |
|--------|------|---------|
| POST | `/pools/create` | Create pool + launch agents |
| GET | `/pools/{pool_id}/tasks` | List all tasks |
| GET | `/pools/{pool_id}/tasks/request` | Request next task |
| PUT | `/pools/{pool_id}/tasks/{task_id}/complete` | Complete task |
| PUT | `/pools/{pool_id}/tasks/{task_id}/fail` | Fail task |
| POST | `/pools/{pool_id}/agents/register` | Register horde agent |
| PUT | `/pools/{pool_id}/agents/{agent_id}/state` | Update horde agent state |
| DELETE | `/pools/{pool_id}/agents/{agent_id}` | Unregister horde agent |
| POST | `/pools/{pool_id}/messages/send` | Pool message |
| GET | `/pools/{pool_id}/messages/check` | Poll pool inbox |

</details>

---

## Tech Stack

- **.NET 9** / C#
- **WPF** (Windows Presentation Foundation) for GUI
- **ModelContextProtocol SDK** for MCP stdio transport
- **ASP.NET Core** minimal API for HTTP REST
- **xUnit** for tests
