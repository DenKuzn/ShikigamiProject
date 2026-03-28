using System.Text.Json;

namespace Shikigami.Runner.Services;

/// <summary>
/// Accumulates filtered conversation history across CLI passes.
/// event_log: raw events from each CLI pass.
/// entries: cleaned context used for prompt building.
/// </summary>
public sealed class ShikigamiContextMemory
{
    private readonly List<Dictionary<string, object>> _entries = new();
    private int _flushOffset;

    public IReadOnlyList<Dictionary<string, object>> Entries => _entries;

    /// <summary>
    /// Extract useful entries from raw events (since last flush) into clean context.
    /// </summary>
    public void FlushEvents(List<Dictionary<string, object>> eventLog, int iteration)
    {
        for (var i = _flushOffset; i < eventLog.Count; i++)
        {
            var evt = eventLog[i];
            var type = evt.TryGetValue("type", out var t) ? t.ToString() : null;

            switch (type)
            {
                case "thinking":
                    var thinkText = evt.TryGetValue("text", out var tt) ? tt.ToString() ?? "" : "";
                    if (!string.IsNullOrEmpty(thinkText))
                        _entries.Add(new() { ["role"] = "thinking", ["text"] = thinkText });
                    break;

                case "tool":
                    _entries.Add(new()
                    {
                        ["role"] = "tool_call",
                        ["name"] = evt.TryGetValue("name", out var n) ? n.ToString() ?? "" : "",
                        ["input"] = evt.TryGetValue("detail", out var d) ? d.ToString() ?? "" : "",
                    });
                    break;

                case "tool_result":
                    _entries.Add(new()
                    {
                        ["role"] = "tool_result",
                        ["content"] = evt.TryGetValue("content", out var c) ? c.ToString() ?? "" : "",
                    });
                    break;

                case "text":
                    var text = evt.TryGetValue("text", out var tx) ? tx.ToString() ?? "" : "";
                    if (!string.IsNullOrEmpty(text))
                        _entries.Add(new() { ["role"] = "text", ["text"] = text });
                    break;
            }
        }
        _flushOffset = eventLog.Count;
        _entries.Add(new() { ["role"] = "turn_boundary", ["turn"] = iteration });
    }

    public void AddUserInput(string text)
    {
        _entries.Add(new() { ["role"] = "user", ["text"] = text });
    }

    public void AddUserStop(string text)
    {
        _entries.Add(new() { ["role"] = "user_stop", ["text"] = text });
    }

    public void AddMessage(string text)
    {
        _entries.Add(new() { ["role"] = "mcp_message", ["text"] = text });
    }

    public void Clear()
    {
        _entries.Clear();
        _flushOffset = 0;
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(_entries, new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}
