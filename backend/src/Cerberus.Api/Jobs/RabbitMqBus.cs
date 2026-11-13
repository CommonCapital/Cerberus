using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Cerberus.Jobs;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Cerberus.Api.Jobs;

/// <param name="Url">The broker's address, <c>amqp://user:password@host:5672/vhost</c> (CERBERUS_RABBITMQ_URL). Never logged or shown.</param>
/// <param name="Prefetch">How many chat turns a worker may hold unacknowledged at once.</param>
public sealed record RabbitMqOptions(string? Url, ushort Prefetch = 32)
{
    public bool Enabled => !string.IsNullOrWhiteSpace(Url);

    /// <summary>Where the broker is, for a log line or an operator: host and port, nothing that is a secret.</summary>
    public string Where => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? $"{uri.Host}:{(uri.Port > 0 ? uri.Port : 5672)}" : "an address that cannot be read";
}

/// <summary>
/// The queue on RabbitMQ (PLAN.md §22): the same producers and workers as in one process, with a broker
/// between accepting work and doing it.
/// <list type="bullet">
/// <item>Queues are durable and messages persistent; a publish returns when the broker has confirmed it.</item>
/// <item>A message is acknowledged by the worker only once the work has reached a final state. A worker that
/// dies before that gets it again — harmless, because work that is no longer waiting is not started twice.</item>
/// <item>A message is two ids — the work's and its user's. Anything else that arrives is rejected, not run.</item>
/// <item>A broker that is away does not lose work: what could not be published is kept and tried again, and
/// what is stored as waiting is published again when the service starts. Consumers reconnect on their own.</item>
/// </list>
/// Written against the client library and the protocol; no broker was available where it was written
/// (see PLAN.md §17 for how to verify it).
/// </summary>
public sealed class RabbitMqBus(RabbitMqOptions options, ILogger<RabbitMqBus> logger) : IMessageBus, IAsyncDisposable
{
    public const string MessageType = "cerberus.work.v1";
    public const int MaxIdLength = 100;
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _connecting = new(1, 1);
    private readonly SemaphoreSlim _publishing = new(1, 1);
    private readonly ConcurrentQueue<(string Queue, WorkMessage Message)> _parked = new();
    private readonly CancellationTokenSource _stopping = new();
    private IConnection? _connection;
    private IChannel? _publisher;
    private int _retrying;

    public string Transport => "RabbitMQ";

    /// <summary>What could not be handed to the broker yet and is waiting to be tried again.</summary>
    public int Parked => _parked.Count;

    public static byte[] Encode(WorkMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(new { id = message.Id, userId = message.UserId });

    /// <summary>The message in a body, or null when the body is not one: not JSON, not two ids, or ids of no sensible length.</summary>
    public static WorkMessage? Decode(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var id = root.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            var userId = root.TryGetProperty("userId", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            return id is { Length: > 0 and <= MaxIdLength } && userId is { Length: > 0 and <= MaxIdLength } ? new WorkMessage(id, userId) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async ValueTask<string?> ProblemAsync(CancellationToken ct)
    {
        try
        {
            var connection = await ConnectionAsync(ct);
            return connection.IsOpen
                ? _parked.IsEmpty ? null : $"{_parked.Count} message(s) are waiting to be handed to the broker"
                : "the connection to the broker is down; reconnecting";
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return "the broker cannot be reached";
        }
    }

    public async ValueTask PublishAsync(string queue, WorkMessage message, CancellationToken ct = default)
    {
        if (await TryPublishAsync(queue, message, ct)) return;
        // Not lost: the work is stored as waiting, and this message is tried again until the broker takes it.
        _parked.Enqueue((queue, message));
        if (Interlocked.Exchange(ref _retrying, 1) == 0) _ = RetryParkedAsync();
    }

    private async Task<bool> TryPublishAsync(string queue, WorkMessage message, CancellationToken ct)
    {
        await _publishing.WaitAsync(ct);
        try
        {
            if (_publisher is not { IsOpen: true })
            {
                if (_publisher is not null) await _publisher.DisposeAsync();
                var connection = await ConnectionAsync(ct);
                // With confirmations a publish returns only when the broker has the message.
                _publisher = await connection.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);
                foreach (var name in WorkQueues.All) await DeclareAsync(_publisher, name, ct);
            }
            var properties = new BasicProperties { Persistent = true, ContentType = "application/json", Type = MessageType, MessageId = message.Id };
            await _publisher.BasicPublishAsync(exchange: "", routingKey: queue, mandatory: true, basicProperties: properties, body: Encode(message), cancellationToken: ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not hand a message to the broker at {Where} ({Problem}); it is kept and tried again.", options.Where, ex.GetType().Name);
            return false;
        }
        finally
        {
            _publishing.Release();
        }
    }

    private async Task RetryParkedAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested && !_parked.IsEmpty)
            {
                await Task.Delay(RetryEvery, _stopping.Token);
                for (var left = _parked.Count; left > 0 && _parked.TryDequeue(out var parked); left--)
                {
                    if (await TryPublishAsync(parked.Queue, parked.Message, _stopping.Token)) continue;
                    _parked.Enqueue(parked);
                    break;   // still away: wait before the next try
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down: what is parked is stored as waiting and is published again at the next start.
        }
        finally
        {
            Interlocked.Exchange(ref _retrying, 0);
            if (!_stopping.IsCancellationRequested && !_parked.IsEmpty && Interlocked.Exchange(ref _retrying, 1) == 0) _ = RetryParkedAsync();
        }
    }

    public async IAsyncEnumerable<Delivery> ConsumeAsync(string queue, [EnumeratorCancellation] CancellationToken ct)
    {
        var inbox = Channel.CreateUnbounded<Delivery>();
        _ = PumpAsync(queue, inbox.Writer, ct);
        await foreach (var delivery in inbox.Reader.ReadAllAsync(ct)) yield return delivery;
    }

    /// <summary>Keeps one consumer on the queue. The client restores it after a lost connection; a broker that was never there is waited for.</summary>
    private async Task PumpAsync(string queue, ChannelWriter<Delivery> inbox, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var connection = await ConnectionAsync(ct);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
                await DeclareAsync(channel, queue, ct);
                // A job consumer does one job at a time and must not sit on others; turns wait in the worker for their place.
                await channel.BasicQosAsync(0, queue == WorkQueues.Jobs ? (ushort)1 : Math.Max((ushort)1, options.Prefetch), false, ct);

                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivered) =>
                {
                    var tag = delivered.DeliveryTag;
                    if (Decode(delivered.Body.Span) is not { } message)
                    {
                        // Not one of ours. Rejected without requeueing, so it cannot come round for ever.
                        logger.LogWarning("A message on {Queue} was not a work message and was rejected.", queue);
                        await channel.BasicNackAsync(tag, multiple: false, requeue: false);
                        return;
                    }
                    inbox.TryWrite(new Delivery(message, async () =>
                    {
                        try
                        {
                            await channel.BasicAckAsync(tag, multiple: false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // The channel went away with the acknowledgement: the broker delivers the message
                            // again, and the work, being finished, is not started a second time.
                            logger.LogInformation("An acknowledgement on {Queue} did not reach the broker ({Problem}).", queue, ex.GetType().Name);
                        }
                    }));
                };
                // Closed for good (not a connection the client is restoring): start over with a new channel.
                channel.ChannelShutdownAsync += (_, reason) =>
                {
                    if (reason.Initiator != ShutdownInitiator.Library || !connection.IsOpen) return Task.CompletedTask;
                    closed.TrySetResult();
                    return Task.CompletedTask;
                };
                await channel.BasicConsumeAsync(queue, autoAck: false, consumer, ct);
                logger.LogInformation("Consuming {Queue} from the broker at {Where}.", queue, options.Where);
                await closed.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("The queue {Queue} at {Where} cannot be consumed ({Problem}); trying again.", queue, options.Where, ex.GetType().Name);
            }
            try
            {
                await Task.Delay(RetryEvery, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        inbox.TryComplete();
    }

    private static Task DeclareAsync(IChannel channel, string queue, CancellationToken ct) =>
        channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, arguments: null, cancellationToken: ct);

    private async Task<IConnection> ConnectionAsync(CancellationToken ct)
    {
        if (_connection is { } open) return open;
        await _connecting.WaitAsync(ct);
        try
        {
            if (_connection is { } meanwhile) return meanwhile;
            var factory = new ConnectionFactory
            {
                Uri = new Uri(options.Url ?? throw new InvalidOperationException("No broker address is configured.")),
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                NetworkRecoveryInterval = RetryEvery,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(10),
                ClientProvidedName = "cerberus",
            };
            return _connection = await factory.CreateConnectionAsync(ct);
        }
        finally
        {
            _connecting.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        try
        {
            if (_publisher is not null) await _publisher.DisposeAsync();
            if (_connection is not null) await _connection.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Leaving: a broker that is already gone is no reason to fail.
        }
        _stopping.Dispose();
    }
}
