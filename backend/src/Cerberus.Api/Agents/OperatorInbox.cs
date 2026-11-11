using System.Collections.Concurrent;

namespace Cerberus.Api.Agents;

/// <summary>
/// Messages from the operator to agents that are running (PLAN.md §6.5,
/// interrupt and steer). A run that can be steered opens an inbox for its
/// job; a message posted while it is open is read by the agent before its
/// next step. In memory only: a message is for the run in progress.
/// </summary>
public sealed class OperatorInbox
{
    public const int MaxMessageLength = 2000;
    public const int MaxPending = 20;

    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _open = new();

    /// <summary>Starts taking messages for a job. Dispose the result when the agent stops.</summary>
    public Session Open(string jobId)
    {
        _open[jobId] = new ConcurrentQueue<string>();
        return new Session(this, jobId);
    }

    /// <summary>False when no agent of that job is taking messages, or too many are already waiting.</summary>
    public bool TryPost(string jobId, string text)
    {
        if (!_open.TryGetValue(jobId, out var queue) || queue.Count >= MaxPending) return false;
        queue.Enqueue(text);
        // Closed in between: the message will not be read, so it was not taken.
        return _open.ContainsKey(jobId);
    }

    private IReadOnlyList<string> Drain(string jobId)
    {
        var messages = new List<string>();
        if (_open.TryGetValue(jobId, out var queue))
        {
            while (queue.TryDequeue(out var message)) messages.Add(message);
        }
        return messages;
    }

    public sealed class Session(OperatorInbox inbox, string jobId) : IDisposable
    {
        private IReadOnlyList<string> _unread = [];

        /// <summary>The messages that arrived since the last call, oldest first.</summary>
        public IReadOnlyList<string> Take() => inbox.Drain(jobId);

        /// <summary>Messages that arrived too late to be read; known after <see cref="Dispose"/>.</summary>
        public IReadOnlyList<string> Unread => _unread;

        public void Dispose()
        {
            if (inbox._open.TryRemove(jobId, out var queue)) _unread = [.. queue];
        }
    }
}
