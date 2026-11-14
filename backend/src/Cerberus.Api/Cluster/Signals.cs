using System.Collections.Concurrent;
using System.Text.Json;
using Cerberus.Api.Agents;
using Cerberus.Api.Chat;
using Cerberus.Api.Jobs;
using Cerberus.Jobs;
using StackExchange.Redis;

namespace Cerberus.Api.Cluster;

/// <summary>What one instance tells the others.</summary>
public static class Topics
{
    /// <summary>Stop a running job. Payload: the job's id.</summary>
    public const string CancelJob = "cancel-job";

    /// <summary>Stop a chat turn that is being answered. Payload: the turn's id.</summary>
    public const string StopTurn = "stop-turn";

    /// <summary>The operator said something to a running agent. Payload: <c>{ jobId, text }</c>.</summary>
    public const string JobMessage = "job-message";

    /// <summary>A question was answered. Payload: <c>{ questionId, selected, text }</c>.</summary>
    public const string Answer = "question-answered";

    public static readonly IReadOnlyList<string> All = [CancelJob, StopTurn, JobMessage, Answer];
}

/// <summary>
/// A word from one instance to all of them (PLAN.md §22 item 7). A request can arrive at any instance, but a
/// running job, a turn being answered and a job waiting for an answer each live in exactly one: the instance
/// that got the request passes it on, and the one that has the work acts on it. The others hear it and have
/// nothing to do. The decision itself is in the database before anything is said here — a signal that is
/// lost costs a delay, never the record of what was decided.
/// </summary>
public interface ISignals
{
    /// <summary>False with one instance: there is nobody else to tell, and callers keep their single-process answers.</summary>
    bool Distributed { get; }

    /// <summary>Tells every instance. False when it could not be said.</summary>
    Task<bool> SendAsync(string topic, string payload);

    Task<IAsyncDisposable> SubscribeAsync(string topic, Func<string, Task> handler);
}

/// <summary>One instance: nothing travels.</summary>
public sealed class LocalSignals : ISignals
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Func<string, Task>>> _handlers = new();

    public bool Distributed => false;

    public async Task<bool> SendAsync(string topic, string payload)
    {
        if (!_handlers.TryGetValue(topic, out var handlers)) return true;
        foreach (var handler in handlers.Values) await handler(payload);
        return true;
    }

    public Task<IAsyncDisposable> SubscribeAsync(string topic, Func<string, Task> handler)
    {
        var id = Guid.NewGuid();
        var handlers = _handlers.GetOrAdd(topic, _ => new());
        handlers[id] = handler;
        return Task.FromResult<IAsyncDisposable>(new Subscription(() =>
        {
            handlers.TryRemove(id, out _);
            return ValueTask.CompletedTask;
        }));
    }

    private sealed class Subscription(Func<ValueTask> end) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => end();
    }
}

/// <summary>Several instances: over Redis publish / subscribe. The client restores subscriptions after a lost connection.</summary>
public sealed class RedisSignals(RedisLink link, ILogger<RedisSignals> logger) : ISignals
{
    public bool Distributed => true;

    private static RedisChannel Channel(string topic) => RedisChannel.Literal($"{RedisOptions.Prefix}signal:{topic}");

    public async Task<bool> SendAsync(string topic, string payload)
    {
        try
        {
            await (await link.ConnectionAsync()).GetSubscriber().PublishAsync(Channel(topic), payload);
            return true;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning("A {Topic} signal could not be sent ({Problem}).", topic, ex.GetType().Name);
            return false;
        }
    }

    public async Task<IAsyncDisposable> SubscribeAsync(string topic, Func<string, Task> handler)
    {
        var queue = await (await link.ConnectionAsync()).GetSubscriber().SubscribeAsync(Channel(topic));
        queue.OnMessage(async message =>
        {
            try
            {
                if (message.Message.HasValue) await handler(message.Message.ToString());
            }
            catch (Exception ex)
            {
                // One bad message must not end the subscription.
                logger.LogWarning("A {Topic} signal could not be acted on ({Problem}).", topic, ex.GetType().Name);
            }
        });
        return new Subscription(queue);
    }

    private sealed class Subscription(ChannelMessageQueue queue) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await queue.UnsubscribeAsync();
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
            {
                // Leaving anyway.
            }
        }
    }
}

/// <summary>
/// Listens to what the other instances say and acts on what concerns work that lives here: stops the job or
/// the turn if it is running in this process, hands the message to the agent if its inbox is open here, wakes
/// the job that is waiting for the answer if it waits here. Everything else is somebody else's and is ignored.
/// </summary>
public sealed class SignalRouter(
    ISignals signals, JobCancellationRegistry jobs, TurnRegistry turns, OperatorInbox inbox, OperatorDesk desk,
    ILogger<SignalRouter> logger) : BackgroundService
{
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    public sealed record JobMessageSignal(string JobId, string Text);

    public sealed record AnswerSignal(string QuestionId, List<string> Selected, string? Text);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!signals.Distributed) return;   // one instance: requests reach the work directly

        var subscriptions = new List<IAsyncDisposable>();
        try
        {
            // A Redis that is not there yet is waited for; until then this instance simply hears nothing.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    subscriptions.Add(await signals.SubscribeAsync(Topics.CancelJob, id => Do(() => jobs.Cancel(id))));
                    subscriptions.Add(await signals.SubscribeAsync(Topics.StopTurn, id => Do(() => turns.TryStop(id))));
                    subscriptions.Add(await signals.SubscribeAsync(Topics.JobMessage, payload => Do(() =>
                    {
                        if (JsonSerializer.Deserialize<JobMessageSignal>(payload, Json) is { JobId.Length: > 0, Text.Length: > 0 } said) inbox.TryPost(said.JobId, said.Text);
                    })));
                    subscriptions.Add(await signals.SubscribeAsync(Topics.Answer, payload => Do(() =>
                    {
                        if (JsonSerializer.Deserialize<AnswerSignal>(payload, Json) is { QuestionId.Length: > 0 } answered)
                        {
                            desk.Deliver(answered.QuestionId, new OperatorAnswer(answered.Selected ?? [], answered.Text));
                        }
                    })));
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Signals from other instances cannot be heard yet ({Problem}); trying again.", ex.GetType().Name);
                    foreach (var subscription in subscriptions) await subscription.DisposeAsync();
                    subscriptions.Clear();
                    await Task.Delay(RetryEvery, stoppingToken);
                }
            }
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            foreach (var subscription in subscriptions) await subscription.DisposeAsync();
        }
    }

    private static Task Do(Action act)
    {
        act();
        return Task.CompletedTask;
    }
}
