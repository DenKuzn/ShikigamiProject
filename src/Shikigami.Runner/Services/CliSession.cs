using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Shikigami.Runner.Services;

/// <summary>
/// Persistent Claude CLI session using --input-format stream-json.
///
/// Lifecycle: Start() → SendMessage()* → Close()/Kill()
///
/// Unlike CliRunner which launched a new process per message,
/// CliSession keeps one process alive and sends messages via stdin NDJSON.
/// Context is maintained by the CLI harness internally.
///
/// Crash recovery: Kill() → Restart(resume: true) → SendMessage("continue")
/// Resume uses --resume {sessionId} to restore full conversation context.
/// </summary>
public sealed class CliSession : IDisposable
{
    private readonly string? _agent;
    private readonly string? _model;
    private readonly string? _tools;
    private readonly string? _workdir;
    private readonly string? _effort;
    private readonly string _sessionId;

    private Process? _proc;
    private bool _resumeMode;
    private bool _disposed;
    private readonly StringBuilder _stderr = new();

    /// <summary>
    /// Max time to wait for a single stdout line before declaring the process hung.
    /// Generous because tool execution (builds, searches) can take minutes.
    /// </summary>
    private static readonly TimeSpan ReadLineTimeout = TimeSpan.FromMinutes(600);

    public CliSession(string? agent = null, string? model = null, string? tools = null,
                      string? workdir = null, string? effort = null, string? sessionId = null)
    {
        _agent = agent;
        _model = model;
        _tools = tools;
        _workdir = workdir;
        _effort = effort;
        _sessionId = sessionId ?? Guid.NewGuid().ToString();
    }

    /// <summary>UUID used for --session-id / --resume.</summary>
    public string SessionId => _sessionId;

    /// <summary>True if the CLI process is running.</summary>
    public bool IsAlive => _proc is { HasExited: false };

    // ════════════════════════════════════════════════════════════════
    //  Lifecycle
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Launch the claude CLI process. Does NOT send any message —
    /// the process waits for the first stdin NDJSON line.
    /// </summary>
    public void Start()
    {
        if (_proc is { HasExited: false })
            throw new InvalidOperationException("Session already running");

        var claudeBin = FindClaude();
        var args = new List<string>
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--strict-mcp-config",
        };

        if (_resumeMode)
            args.AddRange(["--resume", _sessionId]);
        else
            args.AddRange(["--session-id", _sessionId]);

        if (!string.IsNullOrEmpty(_agent))
            args.AddRange(["--agent", _agent]);
        else if (!string.IsNullOrEmpty(_model))
            args.AddRange(["--model", _model]);
        if (!string.IsNullOrEmpty(_tools))
            args.AddRange(["--allowedTools", _tools]);
        if (!string.IsNullOrEmpty(_effort))
            args.AddRange(["--effort", _effort]);

        var psi = new ProcessStartInfo
        {
            FileName = claudeBin,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        if (!string.IsNullOrEmpty(_workdir)) psi.WorkingDirectory = _workdir;

        // Clean env — prevent interference from parent Claude Code process
        foreach (var key in new[] { "CLAUDECODE", "CLAUDE_CODE_SSE_PORT",
                     "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_MAX_OUTPUT_TOKENS" })
            psi.Environment.Remove(key);

        _stderr.Clear();
        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start claude process");

        // Capture stderr asynchronously for diagnostics
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) _stderr.AppendLine(e.Data);
        };
        _proc.BeginErrorReadLine();
    }

    /// <summary>Last captured stderr output (for diagnostics after crash).</summary>
    public string LastStderr => _stderr.ToString();

    // ════════════════════════════════════════════════════════════════
    //  Messaging
    // ════════════════════════════════════════════════════════════════

    /// <summary>
    /// Send a user message and block until the turn completes (result event).
    /// The process stays alive after this — call SendMessage again for the next turn.
    /// </summary>
    public RunResult SendMessage(string content, Action<string, Dictionary<string, object>>? onEvent = null)
    {
        if (_proc == null || _proc.HasExited)
            throw new InvalidOperationException("Session not running. Call Start() first or Restart().");

        var emit = onEvent ?? ((_, _) => { });
        var result = new RunResult();

        // Build NDJSON user message
        var msg = JsonSerializer.Serialize(new
        {
            type = "user",
            message = new { role = "user", content }
        });

        // Write as raw UTF-8 bytes to avoid encoding issues with Cyrillic
        var bytes = Encoding.UTF8.GetBytes(msg + "\n");
        _proc.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
        _proc.StandardInput.BaseStream.Flush();

        // Read events until "result" type signals turn completion
        var toolN = 0;
        var textBlocks = new List<string>();

        while (true)
        {
            // Async read with timeout — protects against CLI hang (bug #25629)
            string? line;
            try
            {
                var readTask = _proc.StandardOutput.ReadLineAsync();
                if (readTask.Wait(ReadLineTimeout))
                {
                    line = readTask.Result;
                }
                else
                {
                    // Timeout — process is hung
                    result.Error = $"CLI process hung (no output for {ReadLineTimeout.TotalMinutes:F0} min)";
                    emit("error", new() { ["message"] = result.Error });
                    Kill();
                    break;
                }
            }
            catch
            {
                line = null;
            }

            if (line == null)
            {
                // EOF — process died mid-turn
                var exitCode = _proc.HasExited ? _proc.ExitCode : -1;
                var stderrText = _stderr.ToString().Trim();
                var detail = $"CLI process exited unexpectedly (exit={exitCode})";
                if (!string.IsNullOrEmpty(stderrText))
                    detail += $"\nstderr: {stderrText}";
                result.Error = detail;
                emit("error", new() { ["message"] = result.Error });
                break;
            }

            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            JsonElement evt;
            try { evt = JsonSerializer.Deserialize<JsonElement>(line); }
            catch { continue; }

            var etype = evt.TryGetProperty("type", out var tp) ? tp.GetString() : null;
            var ts = DateTime.Now.ToString("HH:mm:ss");

            // parent_tool_use_id at top level: non-empty means this event is from a sub-agent.
            // RunnerSession routes it to that sub-agent's collapsible block.
            string GetParentTuid()
            {
                if (!evt.TryGetProperty("parent_tool_use_id", out var ptu)) return "";
                return ptu.ValueKind == JsonValueKind.String ? ptu.GetString() ?? "" : "";
            }

            switch (etype)
            {
                case "system":
                    var subtype = evt.TryGetProperty("subtype", out var sp) ? sp.GetString() : null;
                    HandleSystemSubtype(evt, subtype, ts, emit, result);
                    break;

                case "assistant":
                    var aParentTuid = GetParentTuid();
                    if (evt.TryGetProperty("message", out var aMsg))
                    {
                        try
                        {
                            if (aMsg.TryGetProperty("content", out var content2))
                            {
                                foreach (var blk in content2.EnumerateArray())
                                {
                                    var blkType = blk.TryGetProperty("type", out var bt) ? bt.GetString() : null;
                                    switch (blkType)
                                    {
                                        case "tool_use":
                                            toolN++;
                                            result.ToolsUsed++;
                                            var name = blk.TryGetProperty("name", out var np) ? np.GetString() ?? "?" : "?";
                                            var detail = ExtractToolDetail(blk, name);
                                            var fullInput = blk.TryGetProperty("input", out var fip) ? fip.ToString() : "";
                                            var toolUseId = blk.TryGetProperty("id", out var tuidp) ? tuidp.GetString() ?? "" : "";
                                            result.Events.Add(new() { ["type"] = "tool", ["name"] = name, ["detail"] = detail, ["full_input"] = fullInput, ["time"] = ts });
                                            emit("tool", new()
                                            {
                                                ["number"] = toolN,
                                                ["name"] = name,
                                                ["detail"] = detail,
                                                ["tool_use_id"] = toolUseId,
                                                ["parent_tool_use_id"] = aParentTuid,
                                            });
                                            break;
                                        case "thinking":
                                            var thinkText = blk.TryGetProperty("thinking", out var thp) ? thp.GetString() ?? "" : "";
                                            result.Events.Add(new() { ["type"] = "thinking", ["text"] = thinkText, ["time"] = ts });
                                            emit("thinking", new()
                                            {
                                                ["text"] = thinkText,
                                                ["parent_tool_use_id"] = aParentTuid,
                                            });
                                            break;
                                        case "text":
                                            var text = blk.TryGetProperty("text", out var txp) ? txp.GetString()?.Trim() ?? "" : "";
                                            if (!string.IsNullOrEmpty(text))
                                            {
                                                // Sub-agent text doesn't update the parent's "last text" (used for marker scan).
                                                if (string.IsNullOrEmpty(aParentTuid))
                                                {
                                                    result.LastTextBlock = text;
                                                    textBlocks.Add(text);
                                                }
                                                result.Events.Add(new() { ["type"] = "text", ["text"] = text, ["time"] = ts });
                                                emit("text", new()
                                                {
                                                    ["text"] = text,
                                                    ["parent_tool_use_id"] = aParentTuid,
                                                });

                                                if (string.IsNullOrEmpty(aParentTuid)
                                                    && result.MarkedResult == null
                                                    && text.Contains("AGENT_RESULT_END"))
                                                {
                                                    result.MarkedResult = ExtractMarkedResult(string.Join("\n", textBlocks));
                                                    if (result.MarkedResult != null)
                                                        emit("marked_result", new() { ["text"] = result.MarkedResult });
                                                }
                                            }
                                            break;
                                    }
                                }
                            }
                        }
                        catch { /* content processing must not block usage extraction */ }

                        // Usage stats only meaningful from main-agent events; sub-agent usage is
                        // reported separately in tool_use_result.usage.
                        if (string.IsNullOrEmpty(aParentTuid) && aMsg.TryGetProperty("usage", out var usage))
                        {
                            var inp = (usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0)
                                    + (usage.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0)
                                    + (usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0);
                            var outp = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                            result.InputTokens = inp;
                            result.OutputTokens = outp;

                            var cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var crv) ? crv.GetInt32() : 0;
                            emit("usage", new() { ["input_tokens"] = inp, ["output_tokens"] = outp, ["cache_read"] = cacheRead });
                        }
                    }
                    break;

                case "user":
                    // Tool results from the CLI's tool dispatcher. For sub-agent events
                    // these are the sub-agent's tool_results; for main-agent events these
                    // are the parent's. The runner uses parent_tool_use_id to route them.
                    var uParentTuid = GetParentTuid();
                    if (evt.TryGetProperty("message", out var userMsg)
                        && userMsg.TryGetProperty("content", out var userContent)
                        && userContent.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var blk in userContent.EnumerateArray())
                        {
                            var blkType = blk.TryGetProperty("type", out var ubt) ? ubt.GetString() : null;
                            if (blkType == "tool_result")
                            {
                                var trContent = blk.TryGetProperty("content", out var trc) ? trc.ToString() : "";
                                var trToolId = blk.TryGetProperty("tool_use_id", out var tid) ? tid.GetString() ?? "" : "";
                                var trIsError = blk.TryGetProperty("is_error", out var trie) && trie.ValueKind == JsonValueKind.True;
                                result.Events.Add(new() { ["type"] = "tool_result", ["content"] = trContent, ["tool_use_id"] = trToolId, ["time"] = ts });
                                emit("tool_result", new()
                                {
                                    ["tool_use_id"] = trToolId,
                                    ["content"] = trContent,
                                    ["is_error"] = trIsError,
                                    ["parent_tool_use_id"] = uParentTuid,
                                });
                            }
                        }
                    }
                    break;

                case "result":
                    result.ResultText = evt.TryGetProperty("result", out var rp) ? rp.GetString() ?? "" : "";
                    result.Cost = evt.TryGetProperty("total_cost_usd", out var cp) ? cp.GetDouble() : null;
                    if (evt.TryGetProperty("modelUsage", out var mu))
                    {
                        foreach (var prop in mu.EnumerateObject())
                        {
                            var md = prop.Value;
                            if (md.TryGetProperty("contextWindow", out var cw))
                                result.ContextWindow = cw.GetInt32();

                            if (result.InputTokens == 0)
                            {
                                var muInp = (md.TryGetProperty("inputTokens", out var mi) ? mi.GetInt32() : 0)
                                          + (md.TryGetProperty("cacheCreationInputTokens", out var mcc) ? mcc.GetInt32() : 0)
                                          + (md.TryGetProperty("cacheReadInputTokens", out var mcr) ? mcr.GetInt32() : 0);
                                var muOutp = md.TryGetProperty("outputTokens", out var mo) ? mo.GetInt32() : 0;
                                if (muInp > 0) result.InputTokens = muInp;
                                if (muOutp > 0) result.OutputTokens = muOutp;
                            }
                            break;
                        }
                    }
                    var isError = evt.TryGetProperty("is_error", out var ie) && ie.GetBoolean();
                    emit("result", new()
                    {
                        ["cost"] = result.Cost ?? 0.0,
                        ["is_error"] = isError,
                        ["context_window"] = result.ContextWindow,
                        ["input_tokens"] = result.InputTokens,
                        ["output_tokens"] = result.OutputTokens,
                    });

                    // DO NOT WaitForExit — process stays alive for next message
                    break;

                case "rate_limit_event":
                    // Informational — no action needed
                    break;
            }

            // Break out of read loop when result received
            if (etype == "result") break;
        }

        // Fallback marker extraction
        if (result.MarkedResult == null)
            result.MarkedResult = ExtractMarkedResult(string.Join("\n", textBlocks));

        return result;
    }

    // ════════════════════════════════════════════════════════════════
    //  Control
    // ════════════════════════════════════════════════════════════════

    /// <summary>Kill the CLI process tree (stop button).</summary>
    public void Kill()
    {
        var proc = _proc;
        if (proc == null || proc.HasExited) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/T /F /PID {proc.Id}",
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.WaitForExit(5000);
        }
        catch
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>Close stdin gracefully. Process should exit on its own.</summary>
    public void Close()
    {
        var proc = _proc;
        if (proc == null || proc.HasExited) return;

        try
        {
            proc.StandardInput.Close();
            proc.WaitForExit(10_000);
        }
        catch { }

        if (!proc.HasExited)
            Kill();
    }

    /// <summary>
    /// Kill the current process and relaunch.
    /// If resume=true, uses --resume to restore conversation context from saved session.
    /// </summary>
    public void Restart(bool resume)
    {
        Kill();
        _proc?.WaitForExit(5000);
        _proc = null;

        _resumeMode = resume;
        Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        _proc?.Dispose();
    }

    // ════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════

    private static string? ExtractMarkedResult(string allText)
    {
        const string begin = "AGENT_RESULT_BEGIN";
        const string end = "AGENT_RESULT_END";
        var beginIdx = allText.IndexOf(begin);
        var endIdx = allText.LastIndexOf(end);
        if (beginIdx < 0 || endIdx <= beginIdx) return null;

        return allText[(beginIdx + begin.Length)..endIdx].Trim();
    }

    private static string ExtractToolDetail(JsonElement blk, string name)
    {
        if (!blk.TryGetProperty("input", out var inp)) return "";
        return name switch
        {
            "Bash" => inp.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "",
            "Read" => inp.TryGetProperty("file_path", out var f) ? f.GetString() ?? "" : "",
            "Write" => inp.TryGetProperty("file_path", out var w) ? w.GetString() ?? "" : "",
            "Edit" => inp.TryGetProperty("file_path", out var e) ? e.GetString() ?? "" : "",
            "Glob" => inp.TryGetProperty("pattern", out var g) ? g.GetString() ?? "" : "",
            "Grep" => inp.TryGetProperty("pattern", out var gr) ? gr.GetString() ?? "" : "",
            "Agent" => inp.TryGetProperty("description", out var ad) ? ad.GetString() ?? "" : "",
            _ => "",
        };
    }

    private static void HandleSystemSubtype(
        JsonElement evt, string? subtype, string ts,
        Action<string, Dictionary<string, object>> emit, RunResult result)
    {
        switch (subtype)
        {
            case "init":
                var model = evt.TryGetProperty("model", out var mp) ? mp.GetString() ?? "?" : "?";
                result.Events.Add(new() { ["type"] = "system", ["model"] = model, ["time"] = ts });
                emit("system", new() { ["model"] = model });
                break;

            case "task_started":
            {
                var toolUseId = evt.TryGetProperty("tool_use_id", out var tu) ? tu.GetString() ?? "" : "";
                var taskId = evt.TryGetProperty("task_id", out var ti) ? ti.GetString() ?? "" : "";
                var description = evt.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var taskType = evt.TryGetProperty("task_type", out var tt) ? tt.GetString() ?? "" : "";
                var prompt = evt.TryGetProperty("prompt", out var p) ? p.GetString() ?? "" : "";
                emit("subagent_start", new()
                {
                    ["tool_use_id"] = toolUseId,
                    ["task_id"] = taskId,
                    ["description"] = description,
                    ["task_type"] = taskType,
                    ["prompt"] = prompt,
                });
                break;
            }

            case "task_progress":
            {
                var toolUseId = evt.TryGetProperty("tool_use_id", out var tu) ? tu.GetString() ?? "" : "";
                var description = evt.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var lastTool = evt.TryGetProperty("last_tool_name", out var lt) ? lt.GetString() ?? "" : "";
                emit("subagent_progress", new()
                {
                    ["tool_use_id"] = toolUseId,
                    ["description"] = description,
                    ["last_tool_name"] = lastTool,
                });
                break;
            }

            case "task_notification":
            {
                var toolUseId = evt.TryGetProperty("tool_use_id", out var tu) ? tu.GetString() ?? "" : "";
                var status = evt.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                var summary = evt.TryGetProperty("summary", out var su) ? su.GetString() ?? "" : "";
                int durationMs = 0;
                if (evt.TryGetProperty("usage", out var u)
                    && u.TryGetProperty("duration_ms", out var dm)
                    && dm.ValueKind == JsonValueKind.Number)
                    durationMs = dm.GetInt32();
                emit("subagent_end", new()
                {
                    ["tool_use_id"] = toolUseId,
                    ["status"] = status,
                    ["summary"] = summary,
                    ["duration_ms"] = durationMs,
                });
                break;
            }

            // Other subtypes (status, hook_started, hook_response) are ignored for now.
        }
    }

    private static string FindClaude()
    {
        foreach (var name in new[] { "claude", "claude.cmd", "claude.exe" })
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
        }
        return "claude";
    }
}
