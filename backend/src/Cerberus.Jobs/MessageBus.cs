using System.Threading.Channels;

namespace Cerberus.Jobs;

/// <summary>A unit of work handed to the workers. The job's details live in the database.</summary>
public sealed record JobMessage(string JobId);

/// <summary>
/// The seam between whoever accepts work and whoever executes it (PLAN.md §5).
/// Local mode uses <see cref="InProcMessageBus"/>; production swaps in a
/// RabbitMQ-backed implementation without touching producers or workers.
/// </summary>
public interface IMessageBus
{
    ValueTask PublishAsync(JobMessage message, CancellationToken ct = default);

    /// <summary>Messages in arrival order; each message is delivered to exactly one consumer.</summary>
    IAsyncEnumerable<JobMessage> ConsumeAsync(CancellationToken ct);
}

/// <summary>
/// In-memory bus for a single process. Messages are lost on restart, so the
/// worker re-publishes still-queued jobs from the database when it starts.
/// </summary>
public sealed class InProcMessageBus : IMessageBus
{
    private readonly Channel<JobMessage> _channel = Channel.CreateUnbounded<JobMessage>();

    public ValueTask PublishAsync(JobMessage message, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(message, ct);

    public IAsyncEnumerable<JobMessage> ConsumeAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
