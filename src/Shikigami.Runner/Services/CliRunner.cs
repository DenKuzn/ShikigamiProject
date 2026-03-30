using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Shikigami.Runner.Services;

/// <summary>
/// Result of a single Claude CLI pass.
/// </summary>
public sealed class RunResult
{
    public string ResultText { get; set; } = "";
    public string LastTextBlock { get; set; } = "";
    public int ToolsUsed { get; set; }
    public double? Cost { get; set; }
    public List<Dictionary<string, object>> Events { get; set; } = new();
    public string? Error { get; set; }
    public int ContextWindow { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}

/// <summary>
/// Launches claude CLI as a subprocess, feeds it a prompt, and parses stream-json events.
/// Equivalent to Python cli_runner.py.
/// </summary>
public sealed class CliRunner
{
    private readonly string? _agent;
    private readonly string? _model;
    private readonly string? _tools;
    private readonly string? _workdir;
    private readonly string? _effort;
    private Process? _proc;

    public CliRunner(string? agent = null, string? model = null, string? tools = null,
                     string? workdir = null, string? effort = null)
    {
        _agent = agent;
        _model = model;
        _tools = tools;
        _workdir = workdir;
        _effort = effort;
    }

    /// <summary>
    /// Kill the running CLI process tree.
    /// </summary>
    public void Kill()
    {
        var proc = _proc;
        if (proc == null || proc.HasExited) return;

        try
        {
            // taskkill /T kills the whole process tree on Windows
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

    /// <summary>
    /// Run a single CLI pass. Calls onEvent for each parsed stream-json event.
    /// </summary>
    public RunResult Run(string prompt, Action<string, Dictionary<string, object>>? onEvent = null)
    {
        var emit = onEvent ?? ((_, _) => { });
        var result = new RunResult();

        var claudeBin = FindClaude();
        var args = new List<string>
        {
            "-p", "--verbose",
            "--no-session-persistence",
            "--output-format", "stream-json",
            "--strict-mcp-config",
        };
        if (!string.IsNullOrEmpty(_agent))
            args.AddRange(["--agent", _agent]);
        else if (!string.IsNullOrEmpty(_model))
            args.AddRange(["--model", _model]);
        if (!string.IsNullOrEmpty(_tools))
            args.AddRange(["--allowedTools", _tools]);
        if (!string.IsNullOrEmpty(_effort))
            args.AddRange(["--effort", _effort]);

        var cmdLine = $"{claudeBin} {string.Join(" ", args)}";
        emit("command", new() { ["cmd"] = cmdLine });

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = claudeBin,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = System.Text.Encoding.UTF8,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            if (!string.IsNullOrEmpty(_workdir)) psi.WorkingDirectory = _workdir;

            // Clean env
            foreach (var key in new[] { "CLAUDECODE", "CLAUDE_CODE_SSE_PORT",
                         "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_MAX_OUTPUT_TOKENS" })
                psi.Environment.Remove(key);

            _proc = Process.Start(psi);
            if (_proc == null)
            {
                result.Error = "Failed to start claude process";
                return result;
            }

            _proc.StandardInput.Write(prompt);
            _proc.StandardInput.Close();
        }
        catch (Exception e)
        {
            result.Error = $"'claude' CLI not found or failed to start: {e.Message}";
            emit("error", new() { ["message"] = result.Error });
            return result;
        }

        var toolN = 0;
        while (true)
        {
            var line = _proc.StandardOutput.ReadLine();
            if (line == null) break;
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            JsonElement evt;
            try { evt = JsonSerializer.Deserialize<JsonElement>(line); }
            catch { continue; }

            var etype = evt.TryGetProperty("type", out var tp) ? tp.GetString() : null;
            var ts = DateTime.Now.ToString("HH:mm:ss");

            switch (etype)
            {
                case "system":
                    var model = evt.TryGetProperty("model", out var mp) ? mp.GetString() ?? "?" : "?";
                    result.Events.Add(new() { ["type"] = "system", ["model"] = model, ["time"] = ts });
                    emit("system", new() { ["model"] = model });
                    break;

                case "assistant":
                    if (evt.TryGetProperty("message", out var msg))
                    {
                        try
                        {
                            if (msg.TryGetProperty("content", out var content))
                            {
                                foreach (var blk in content.EnumerateArray())
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
                                            result.Events.Add(new() { ["type"] = "tool", ["name"] = name, ["detail"] = detail, ["full_input"] = fullInput, ["time"] = ts });
                                            emit("tool", new() { ["number"] = toolN, ["name"] = name, ["detail"] = detail });
                                            break;
                                        case "thinking":
                                            var thinkText = blk.TryGetProperty("thinking", out var thp) ? thp.GetString() ?? "" : "";
                                            result.Events.Add(new() { ["type"] = "thinking", ["text"] = thinkText, ["time"] = ts });
                                            emit("thinking", new() { ["text"] = thinkText });
                                            break;
                                        case "text":
                                            var text = blk.TryGetProperty("text", out var txp) ? txp.GetString()?.Trim() ?? "" : "";
                                            if (!string.IsNullOrEmpty(text))
                                            {
                                                result.LastTextBlock = text;
                                                result.Events.Add(new() { ["type"] = "text", ["text"] = text, ["time"] = ts });
                                                emit("text", new() { ["text"] = text });
                                            }
                                            break;
                                    }
                                }
                            }
                        }
                        catch { /* content processing must not block usage extraction */ }

                        if (msg.TryGetProperty("usage", out var usage))
                        {
                            var inp = (usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0)
                                    + (usage.TryGetProperty("cache_creation_input_tokens", out var cc) ? cc.GetInt32() : 0)
                                    + (usage.TryGetProperty("cache_read_input_tokens", out var cr) ? cr.GetInt32() : 0);
                            var outp = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                            result.InputTokens = inp;
                            result.OutputTokens = outp;
                        }
                    }
                    break;

                case "user":
                    if (evt.TryGetProperty("message", out var userMsg)
                        && userMsg.TryGetProperty("content", out var userContent))
                    {
                        foreach (var blk in userContent.EnumerateArray())
                        {
                            var blkType = blk.TryGetProperty("type", out var ubt) ? ubt.GetString() : null;
                            if (blkType == "tool_result")
                            {
                                var trContent = blk.TryGetProperty("content", out var trc) ? trc.ToString() : "";
                                var trToolId = blk.TryGetProperty("tool_use_id", out var tid) ? tid.GetString() ?? "" : "";
                                result.Events.Add(new() { ["type"] = "tool_result", ["content"] = trContent, ["tool_use_id"] = trToolId, ["time"] = ts });
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

                            // Extract tokens from modelUsage (camelCase) as fallback
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
                    break;
            }
        }

        _proc.WaitForExit();
        _proc = null;
        return result;
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
            _ => "",
        };
    }

    private static string FindClaude()
    {
        // Try common locations
        foreach (var name in new[] { "claude", "claude.cmd", "claude.exe" })
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                var full = Path.Combine(dir, name);
                if (File.Exists(full)) return full;
            }
        }
        return "claude"; // fallback — let OS resolve
    }
}
