namespace Shikigami.Runner.Services;

/// <summary>
/// Audit log of conversation events across CLI turns.
///
/// With persistent CLI sessions, context is maintained by the CLI harness.
/// This class is no longer used for prompt building — only for:
///   - UI event tracking (what happened during the session)
///   - Event log submission to the MCP server
///   - Horde task boundary tracking
/// </summary>
public sealed class ShikigamiContextMemory
{
    private readonly List<Dictionary<string, object>> _entries = new();
    private int _currentTaskStartIndex;

    public IReadOnlyList<Dictionary<string, object>> Entries => _entries;

    /// <summary>
    /// Mark the beginning of a new horde task.
    /// CurrentTaskJson() will return only entries after this point.
    /// Full history is preserved in Entries / ToJson().
    /// </summary>
    public void BeginTask(string taskId)
    {
        _currentTaskStartIndex = _entries.Count;
        _entries.Add(new() { ["role"] = "task_boundary", ["task_id"] = taskId });
    }

    /// <summary>
    /// Extract useful entries from raw events into clean context.
    /// Each result.Events is a fresh list from a single CLI run.
    /// </summary>
    public void FlushEvents(List<Dictionary<string, object>> eventLog, int iteration)
    {
        for (var i = 0; i < eventLog.Count; i++)
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
                        ["input"] = evt.TryGetValue("full_input", out var fi) ? fi.ToString() ?? "" : "",
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
        _currentTaskStartIndex = 0;
    }

    /// <summary>
    /// Number of entries recorded since the last BeginTask call.
    /// Useful for checking if any work was done on the current task.
    /// </summary>
    public int CurrentTaskEntryCount =>
        _entries.Count > _currentTaskStartIndex + 1
            ? _entries.Count - _currentTaskStartIndex - 1
            : 0;
}
