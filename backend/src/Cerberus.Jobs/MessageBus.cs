using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Cerberus.Jobs;

/// <summary>
/// A unit of work handed to the workers: which work, and whose. Nothing else travels — the work's
/// details live in the database, and the user id is the one the API established when it authenticated
/// the request, never one a client supplied. A worker checks it against the stored work before acting.
/// </summary>
public sealed record WorkMessage(string Id, string UserId);

/// <summary>The queues work travels on.</summary>
public static class WorkQueues
{
    /// <summary>Background jobs (scans, reviews, agent runs).</summary>
    public const string Jobs = "cerberus.jobs";

    /// <summary>Turns of a conversation: one request to the chat agent each.</summary>
    public const string ChatTurns = "cerberus.chat-turns";

    public static readonly IReadOnlyList<string> All = [Jobs, ChatTurns];
}

/// <summary>
/// One message as a worker receives it. <see cref="DoneAsync"/> tells the queue the work has reached a
/// final state and the message need not be delivered again. A worker that dies before saying so gets
/// the message back (where the queue is durable) — which is harmless, because work that is no longer
/// waiting is not started a second time.
/// </summary>
public sealed class Delivery(WorkMessage message, Func<ValueTask>? done = null)
{
    private int _done;

    public WorkMessage Message { get; } = message;

    public ValueTask DoneAsync() =>
        done is not null && Interlocked.Exchange(ref _done, 1) == 0 ? done() : ValueTask.CompletedTask;
}

/// <summary>
/// The seam between whoever accepts work and whoever executes it (PLAN.md §5, §22). Local mode uses
/// <see cref="InProcMessageBus"/>; with a broker configured the same producers and workers run on RabbitMQ.
/// </summary>
public interface IMessageBus
{
    /// <summary>What carries the messages, for the operator ("this process", "RabbitMQ").</summary>
    string Transport { get; }

    ValueTask PublishAsync(string queue, WorkMessage message, CancellationToken ct = default);

    /// <summary>Messages of one queue in arrival order; each is delivered to exactly one consumer.</summary>
    IAsyncEnumerable<Delivery> ConsumeAsync(string queue, CancellationToken ct);
}

/// <summary>
/// In-memory bus for a single process. Messages are lost on restart, so the workers re-publish what is
/// still waiting in the database when they start.
/// </summary>
public sealed class InProcMessageBus : IMessageBus
{
    private readonly ConcurrentDictionary<string, Channel<WorkMessage>> _queues = new();

    public string Transport => "this process";

    private Channel<WorkMessage> Queue(string name) => _queues.GetOrAdd(name, _ => Channel.CreateUnbounded<WorkMessage>());

    public ValueTask PublishAsync(string queue, WorkMessage message, CancellationToken ct = default) =>
        Queue(queue).Writer.WriteAsync(message, ct);

    public async IAsyncEnumerable<Delivery> ConsumeAsync(string queue, [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var message in Queue(queue).Reader.ReadAllAsync(ct)) yield return new Delivery(message);
    }
}
