namespace Shikigami.Core.Models;

/// <summary>
/// Thread-safe message queue. Encapsulates the List + lock pattern
/// so callers never handle raw locking.
/// Replaces the error-prone ConcurrentDictionary&lt;string, List&lt;MessageRecord&gt;&gt; + manual lock.
/// </summary>
public sealed class MessageQueue
{
    private readonly List<MessageRecord> _messages = new();
    private readonly object _lock = new();

    /// <summary>
    /// Add a message to the queue. Thread-safe.
    /// </summary>
    public void Enqueue(MessageRecord msg)
    {
        lock (_lock) _messages.Add(msg);
    }

    /// <summary>
    /// Remove and return all queued messages. Thread-safe.
    /// Returns an empty list if queue is empty.
    /// </summary>
    public List<MessageRecord> DrainAll()
    {
        lock (_lock)
        {
            var copy = new List<MessageRecord>(_messages);
            _messages.Clear();
            return copy;
        }
    }

    /// <summary>
    /// Current number of queued messages. Thread-safe.
    /// </summary>
    public int Count
    {
        get { lock (_lock) return _messages.Count; }
    }
}
