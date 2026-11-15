using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cerberus.Api.Jobs;
using Cerberus.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace Cerberus.Tests;

/// <summary>
/// A real RabbitMQ for the tests that are about RabbitMQ: the machine's own <c>rabbitmq-server</c>, as a node of
/// its own on free ports bound to this machine, with nothing kept afterwards. Where none is installed those
/// tests are skipped and say so — what they check is what the broker itself does with quorum queues, dead
/// letters and cancelled consumers, which a stand-in cannot honestly do.
/// </summary>
public sealed class RabbitServer : IDisposable
{
    public static readonly string? Binary = new[] { "/opt/homebrew/opt/rabbitmq/sbin", "/usr/local/opt/rabbitmq/sbin", "/usr/lib/rabbitmq/bin", "/usr/sbin" }
        .Concat((System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        .Select(dir => Path.Combine(dir, "rabbitmq-server"))
        .FirstOrDefault(File.Exists);

    private readonly string _home = Path.Combine(Path.GetTempPath(), $"cerberus-rabbit-{Guid.NewGuid():n}");
    private readonly string _node = $"cerberus-test-{Guid.NewGuid():n}"[..28] + "@localhost";
    private readonly Process? _process;

    public string Url { get; } = "";

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private Dictionary<string, string> Settings(int port) => new()
    {
        ["RABBITMQ_NODENAME"] = _node,
        ["RABBITMQ_NODE_IP_ADDRESS"] = "127.0.0.1",
        ["RABBITMQ_NODE_PORT"] = port.ToString(),
        ["RABBITMQ_DIST_PORT"] = FreePort().ToString(),
        ["RABBITMQ_MNESIA_BASE"] = Path.Combine(_home, "data"),
        ["RABBITMQ_LOG_BASE"] = Path.Combine(_home, "log"),
        ["RABBITMQ_ENABLED_PLUGINS_FILE"] = Path.Combine(_home, "enabled_plugins"),
        ["RABBITMQ_CONFIG_FILE"] = Path.Combine(_home, "rabbitmq.conf"),
        ["ERL_EPMD_ADDRESS"] = "127.0.0.1",
        ["RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS"] = "-kernel inet_dist_use_interface {127,0,0,1}",
    };

    public RabbitServer()
    {
        if (Binary is null) return;
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "enabled_plugins"), "[].");
        var port = FreePort();
        File.WriteAllText(Path.Combine(_home, "rabbitmq.conf"), $"listeners.tcp.1 = 127.0.0.1:{port}\nloopback_users.guest = true\n");
        var start = new ProcessStartInfo(Binary) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var (name, value) in Settings(port)) start.Environment[name] = value;
        _process = Process.Start(start);
        _process!.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        Url = $"amqp://guest:guest@127.0.0.1:{port}/";

        // Ready when it accepts an AMQP connection, not merely a socket.
        var factory = new ConnectionFactory { Uri = new Uri(Url) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            try
            {
                using var connection = factory.CreateConnectionAsync().GetAwaiter().GetResult();
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline && !_process.HasExited)
            {
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>A connection for the test to look at the broker with, besides the bus under test.</summary>
    public Task<IConnection> ConnectAsync() => new ConnectionFactory { Uri = new Uri(Url) }.CreateConnectionAsync();

    public void Dispose()
    {
        if (_process is null) return;
        try
        {
            var stop = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(Binary)!, "rabbitmqctl"), ["-n", _node, "stop"]) { RedirectStandardOutput = true, RedirectStandardError = true };
            stop.Environment["ERL_EPMD_ADDRESS"] = "127.0.0.1";
            using var stopping = Process.Start(stop);
            stopping?.WaitForExit(20_000);
        }
        catch (Exception)
        {
            // Killed below in any case.
        }
        if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        _process.Dispose();
        try
        {
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
            // A file still held a moment longer; the temp folder is cleaned eventually.
        }
    }
}

public sealed class RabbitFactAttribute : FactAttribute
{
    public RabbitFactAttribute()
    {
        if (RabbitServer.Binary is null) Skip = "rabbitmq-server is not installed on this machine; these tests need a real one.";
    }
}

public sealed class RabbitMqBrokerTests(RabbitServer broker) : IClassFixture<RabbitServer>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private RabbitMqBus Bus() => new(new RabbitMqOptions(broker.Url), NullLogger<RabbitMqBus>.Instance);

    /// <summary>Takes the next delivery of a queue, or fails after a while.</summary>
    private static async Task<Delivery> NextAsync(IAsyncEnumerator<Delivery> consumer)
    {
        var next = consumer.MoveNextAsync().AsTask();
        Assert.True(await Task.WhenAny(next, Task.Delay(Patience)) == next && next.Result, "nothing was delivered");
        return consumer.Current;
    }

    private static async Task<uint> CountAsync(IConnection connection, string queue)
    {
        await using var channel = await connection.CreateChannelAsync();
        return (await channel.QueueDeclarePassiveAsync(queue)).MessageCount;
    }

    private static async Task<string?> TakeRawAsync(IConnection connection, string queue)
    {
        await using var channel = await connection.CreateChannelAsync();
        var deadline = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < deadline)
        {
            if (await channel.BasicGetAsync(queue, autoAck: true) is { } got) return Encoding.UTF8.GetString(got.Body.Span);
            await Task.Delay(100);
        }
        return null;
    }

    [RabbitFact]
    public async Task Work_goes_through_a_quorum_queue_and_is_gone_once_it_is_done()
    {
        await using var bus = Bus();
        using var stop = new CancellationTokenSource(Patience);
        await using var consumer = bus.ConsumeAsync(WorkQueues.ChatTurns, stop.Token).GetAsyncEnumerator(stop.Token);

        await bus.PublishAsync(WorkQueues.ChatTurns, new WorkMessage("turn-1", "user-1"));
        var delivery = await NextAsync(consumer);
        Assert.Equal(new WorkMessage("turn-1", "user-1"), delivery.Message);
        await delivery.DoneAsync();

        await using var look = await broker.ConnectAsync();
        await Task.Delay(300);
        Assert.Equal(0u, await CountAsync(look, WorkQueues.ChatTurns));
        Assert.Null(await bus.ProblemAsync(default));

        // It is a quorum queue: declaring it as a classic one is refused by the broker.
        await using var channel = await look.CreateChannelAsync();
        var refused = await Assert.ThrowsAsync<RabbitMQ.Client.Exceptions.OperationInterruptedException>(() =>
            channel.QueueDeclareAsync(WorkQueues.ChatTurns, durable: true, exclusive: false, autoDelete: false, arguments: null));
        Assert.Equal(Constants.PreconditionFailed, refused.ShutdownReason!.ReplyCode);
    }

    [RabbitFact]
    public async Task What_a_worker_did_not_finish_is_handed_out_again()
    {
        var first = Bus();
        using (var stop = new CancellationTokenSource(Patience))
        {
            await using var consumer = first.ConsumeAsync(WorkQueues.Jobs, stop.Token).GetAsyncEnumerator(stop.Token);
            await first.PublishAsync(WorkQueues.Jobs, new WorkMessage("job-unfinished", "user-1"));
            Assert.Equal("job-unfinished", (await NextAsync(consumer)).Message.Id);
            // The worker dies before it is done: no acknowledgement.
            await first.DisposeAsync();
        }

        await using var second = Bus();
        using var again = new CancellationTokenSource(Patience);
        await using var next = second.ConsumeAsync(WorkQueues.Jobs, again.Token).GetAsyncEnumerator(again.Token);
        var redelivered = await NextAsync(next);
        Assert.Equal(new WorkMessage("job-unfinished", "user-1"), redelivered.Message);
        await redelivered.DoneAsync();
    }

    [RabbitFact]
    public async Task A_message_that_is_not_ours_is_kept_aside_with_the_dead_letters_not_dropped_and_not_looped()
    {
        await using var bus = Bus();
        using var stop = new CancellationTokenSource(Patience);
        await using var consumer = bus.ConsumeAsync(WorkQueues.Jobs, stop.Token).GetAsyncEnumerator(stop.Token);
        await bus.PublishAsync(WorkQueues.Jobs, new WorkMessage("job-real", "user-1"));
        await (await NextAsync(consumer)).DoneAsync();   // the queues exist now

        await using var look = await broker.ConnectAsync();
        await using (var channel = await look.CreateChannelAsync())
        {
            await channel.BasicPublishAsync("", WorkQueues.Jobs, Encoding.UTF8.GetBytes("not a work message"));
        }

        Assert.Equal("not a work message", await TakeRawAsync(look, RabbitMqBus.DeadLetters));
        await Task.Delay(300);
        Assert.Equal(0u, await CountAsync(look, WorkQueues.Jobs));
    }

    [RabbitFact]
    public async Task A_message_that_is_never_finished_is_set_aside_after_the_delivery_limit()
    {
        await using var bus = Bus();
        using var stop = new CancellationTokenSource(Patience);
        await using (var warm = bus.ConsumeAsync(WorkQueues.ChatTurns, stop.Token).GetAsyncEnumerator(stop.Token))
        {
            await bus.PublishAsync(WorkQueues.ChatTurns, new WorkMessage("turn-warm", "user-1"));
            await (await NextAsync(warm)).DoneAsync();
        }
        await bus.DisposeAsync();

        // A worker that takes the message and dies, again and again — here: a channel closed without an acknowledgement.
        await using var look = await broker.ConnectAsync();
        await using (var publish = await look.CreateChannelAsync())
        {
            await publish.BasicPublishAsync("", WorkQueues.ChatTurns, RabbitMqBus.Encode(new WorkMessage("turn-poison", "user-1")));
        }
        var handedOut = 0;
        for (var i = 0; i < RabbitMqBus.DeliveryLimit + 3; i++)
        {
            await using var channel = await look.CreateChannelAsync();
            if (await channel.BasicGetAsync(WorkQueues.ChatTurns, autoAck: false) is null) break;
            handedOut++;
            await channel.CloseAsync();
        }

        Assert.InRange(handedOut, RabbitMqBus.DeliveryLimit, RabbitMqBus.DeliveryLimit + 1);
        Assert.Equal("""{"id":"turn-poison","userId":"user-1"}""", await TakeRawAsync(look, RabbitMqBus.DeadLetters));
    }

    [RabbitFact]
    public async Task A_consumer_the_broker_ends_starts_again_and_declares_its_queue_again()
    {
        await using var bus = Bus();
        using var stop = new CancellationTokenSource(Patience * 2);
        await using var consumer = bus.ConsumeAsync(WorkQueues.Jobs, stop.Token).GetAsyncEnumerator(stop.Token);
        await bus.PublishAsync(WorkQueues.Jobs, new WorkMessage("job-before", "user-1"));
        await (await NextAsync(consumer)).DoneAsync();

        // The queue is deleted while it is being consumed: the broker cancels the consumer.
        await using var look = await broker.ConnectAsync();
        await using (var channel = await look.CreateChannelAsync())
        {
            await channel.QueueDeleteAsync(WorkQueues.Jobs, ifUnused: false, ifEmpty: false);
        }

        // Work published afterwards still arrives: the publisher and the consumer both declared the queue again.
        await bus.PublishAsync(WorkQueues.Jobs, new WorkMessage("job-after", "user-1"));
        var delivered = await NextAsync(consumer);
        Assert.Equal("job-after", delivered.Message.Id);
        await delivered.DoneAsync();
    }

    [RabbitFact]
    public async Task A_queue_left_with_other_settings_by_an_older_version_is_said_plainly_and_not_deleted()
    {
        await using var look = await broker.ConnectAsync();
        // An older version declared the job queue as a plain classic queue — and there is still work in it.
        await using (var channel = await look.CreateChannelAsync())
        {
            await channel.QueueDeleteAsync(WorkQueues.Jobs, ifUnused: false, ifEmpty: false);
            await channel.QueueDeclareAsync(WorkQueues.Jobs, durable: true, exclusive: false, autoDelete: false, arguments: null);
            await channel.BasicPublishAsync("", WorkQueues.Jobs, RabbitMqBus.Encode(new WorkMessage("job-old", "user-1")));
        }

        await using var bus = Bus();
        await bus.PublishAsync(WorkQueues.Jobs, new WorkMessage("job-new", "user-1"));

        Assert.Equal(1, bus.Parked);   // kept, not lost
        Assert.Equal("the queue cerberus.jobs exists on the broker with other settings than this version uses (an older version declared it); " +
                     "once it is empty, delete it and it is declared again", await bus.ProblemAsync(default));
        Assert.Equal(1u, await CountAsync(look, WorkQueues.Jobs));   // its work is still there

        // The operator lets it empty and deletes it: the kept message goes out within a few seconds, and all is well again.
        await using (var channel = await look.CreateChannelAsync())
        {
            await channel.QueueDeleteAsync(WorkQueues.Jobs, ifUnused: false, ifEmpty: false);
        }
        var deadline = DateTime.UtcNow + Patience;
        while (bus.Parked > 0 && DateTime.UtcNow < deadline) await Task.Delay(200);
        Assert.Equal(0, bus.Parked);
        Assert.Null(await bus.ProblemAsync(default));
        Assert.Equal("""{"id":"job-new","userId":"user-1"}""", await TakeRawAsync(look, WorkQueues.Jobs));
    }
}
