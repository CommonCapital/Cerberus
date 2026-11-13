using System.Collections.Concurrent;
using System.Text.Json;
using Cerberus.Api.Auth;
using Cerberus.Api.Hubs;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Chat;

public static class TurnStates
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    public static readonly IReadOnlyList<string> Active = [Queued, Running];

    public static bool IsFinal(string state) => state is Completed or Failed or Cancelled;
}

/// <param name="Concurrency">How many turns are answered at the same time, over all users (CERBERUS_CHAT_CONCURRENCY).</param>
/// <param name="PerUser">How many of one user's turns are answered at the same time (CERBERUS_CHAT_PER_USER): one user's burst cannot take every place.</param>
/// <param name="MaxWaitingPerUser">How many turns one user may have waiting or running before more are refused.</param>
/// <param name="Timeout">How long one turn may take.</param>
/// <param name="KeepFor">How long a finished turn and its events are kept for clients that still have to read them.</param>
public sealed record ChatTurnOptions(
    int Concurrency = 4, int PerUser = 2, int MaxWaitingPerUser = 20, TimeSpan? Timeout = null, TimeSpan? KeepFor = null)
{
    public TimeSpan TurnTimeout => Timeout ?? TimeSpan.FromMinutes(15);
    public TimeSpan Retention => KeepFor ?? TimeSpan.FromHours(24);
}

/// <summary>
/// Delivers one turn's reply to its user and keeps it (PLAN.md §22). Each chunk gets the turn's next sequence
/// number and is pushed to the user's connections at once; chunks are stored in small batches — at once when
/// they are more than a fragment of text. Nothing depends on the push arriving: a client that was away reads
/// what it missed by sequence number, and what is not stored yet is still answered from here.
/// </summary>
public sealed class TurnDelivery(ChatTurn turn, IHubContext<ChatHub> hub, IServiceScopeFactory scopes, ILogger logger)
{
    public const int BatchSize = 32;
    public static readonly TimeSpan BatchAge = TimeSpan.FromMilliseconds(250);

    private readonly object _lock = new();
    private readonly List<ChatTurnEvent> _unstored = [];
    private long _sequence;
    private DateTime _lastStore = DateTime.UtcNow;

    public long LastSequence => Interlocked.Read(ref _sequence);

    public async Task WriteAsync(string chunk, CancellationToken ct)
    {
        ChatTurnEvent record;
        bool store;
        lock (_lock)
        {
            record = new ChatTurnEvent { TurnId = turn.Id, UserId = turn.UserId, Sequence = ++_sequence, Chunk = chunk, CreatedAt = DateTime.UtcNow };
            _unstored.Add(record);
            store = _unstored.Count >= BatchSize || DateTime.UtcNow - _lastStore >= BatchAge || !IsFragment(chunk);
        }
        await PushAsync(hub, turn.UserId, ChatHub.ChatEvent, Describe(turn, record), logger);
        if (store) await StoreAsync();
    }

    /// <summary>What has not reached the database yet, after a sequence number — so that a reader misses nothing.</summary>
    public IReadOnlyList<ChatTurnEvent> Unstored(long after)
    {
        lock (_lock) return _unstored.Where(e => e.Sequence > after).ToList();
    }

    /// <summary>Stores what is waiting. A failure is logged and tried again with the next batch: the reply goes on.</summary>
    public async Task StoreAsync()
    {
        List<ChatTurnEvent> batch;
        lock (_lock)
        {
            if (_unstored.Count == 0) return;
            batch = [.. _unstored];
        }
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
            db.ChatTurnEvents.AddRange(batch);
            await db.SaveChangesAsync();
            lock (_lock)
            {
                _unstored.RemoveAll(e => e.Sequence <= batch[^1].Sequence);
                _lastStore = DateTime.UtcNow;
            }
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not store {Count} event(s) of turn {TurnId}; they are kept in memory.", batch.Count, turn.Id);
        }
    }

    /// <summary>A fragment of text, of which there are hundreds a reply: worth batching. Everything else is stored at once.</summary>
    private static bool IsFragment(string chunk) =>
        chunk.StartsWith("{\"type\":\"text-delta\"", StringComparison.Ordinal) ||
        chunk.StartsWith("{\"type\":\"reasoning-delta\"", StringComparison.Ordinal) ||
        chunk.StartsWith("{\"type\":\"tool-input-delta\"", StringComparison.Ordinal);

    public static object Describe(ChatTurn turn, ChatTurnEvent e) => new
    {
        turnId = turn.Id,
        sessionId = turn.SessionId,
        sequence = e.Sequence,
        chunk = JsonSerializer.Deserialize<JsonElement>(e.Chunk),
    };

    /// <summary>To every connection of the user, and to nobody else. A user with no connection reads it later.</summary>
    public static async Task PushAsync(IHubContext<ChatHub> hub, string userId, string method, object payload, ILogger logger)
    {
        try
        {
            await hub.Clients.Group(ChatHub.GroupFor(userId)).SendAsync(method, payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "A {Method} push failed; it stays readable over HTTP.", method);
        }
    }
}

/// <summary>The turns being answered by this process: so one can be stopped, and its newest events read.</summary>
public sealed class TurnRegistry
{
    public sealed record ActiveTurn(CancellationTokenSource Stop, TurnDelivery Delivery)
    {
        public bool StoppedByUser { get; set; }
    }

    private readonly ConcurrentDictionary<string, ActiveTurn> _active = new();

    public ActiveTurn Add(string turnId, CancellationTokenSource stop, TurnDelivery delivery) => _active[turnId] = new ActiveTurn(stop, delivery);

    public ActiveTurn? Find(string turnId) => _active.GetValueOrDefault(turnId);

    public void Remove(string turnId) => _active.TryRemove(turnId, out _);

    /// <summary>Stops a turn that is being answered here. False when it is not.</summary>
    public bool TryStop(string turnId)
    {
        if (!_active.TryGetValue(turnId, out var turn)) return false;
        turn.StoppedByUser = true;
        try
        {
            turn.Stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It ended in this very moment.
        }
        return true;
    }
}

/// <summary>Places that are handed out per key: one per conversation, a few per user. Whoever comes next waits their turn.</summary>
internal sealed class KeyedGate
{
    private readonly Dictionary<string, (SemaphoreSlim Gate, int Holders)> _gates = [];

    public async Task<IDisposable> EnterAsync(string key, int places, CancellationToken ct)
    {
        SemaphoreSlim gate;
        lock (_gates)
        {
            var entry = _gates.TryGetValue(key, out var known) ? known : (Gate: new SemaphoreSlim(places, places), Holders: 0);
            _gates[key] = (entry.Gate, entry.Holders + 1);
            gate = entry.Gate;
        }
        try
        {
            await gate.WaitAsync(ct);
        }
        catch
        {
            Leave(key, release: false);
            throw;
        }
        return new Place(this, key);
    }

    private void Leave(string key, bool release)
    {
        lock (_gates)
        {
            var (gate, holders) = _gates[key];
            if (release) gate.Release();
            if (holders <= 1) _gates.Remove(key);
            else _gates[key] = (gate, holders - 1);
        }
    }

    private sealed class Place(KeyedGate owner, string key) : IDisposable
    {
        private int _left;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0) owner.Leave(key, release: true);
        }
    }
}

/// <summary>Whether the worker that answers queued turns is alive, for readiness.</summary>
public sealed class ChatWorkerHealth
{
    public DateTime? StartedAt { get; set; }
    public string? Fault { get; set; }
}

/// <summary>
/// Answers the turns that were queued (PLAN.md §22, "dialogue over the queue"). A message names a turn and its
/// user; the turn itself is in the database. The order of a conversation is kept — one turn of a session at a
/// time, the earlier first — and the places are shared fairly: a few per user at a time, so that one user's
/// burst leaves room for everybody else's question. The reply goes to the user's connections as it is written
/// and is stored for whoever was not connected.
/// </summary>
public sealed class ChatTurnWorker(
    IMessageBus bus, IServiceScopeFactory scopes, TurnRegistry registry, ChatTurnOptions options, IHubContext<ChatHub> hub,
    ChatWorkerHealth health, ILogger<ChatTurnWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private readonly KeyedGate _sessions = new();
    private readonly KeyedGate _users = new();
    private readonly SemaphoreSlim _places = new(Math.Max(1, options.Concurrency));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverAsync(stoppingToken);
            health.StartedAt = DateTime.UtcNow;
            var tidying = TidyAsync(stoppingToken);

            await foreach (var delivery in bus.ConsumeAsync(WorkQueues.ChatTurns, stoppingToken))
            {
                // Not awaited: a turn that waits for its conversation or its user's place must not hold up the others.
                _ = HandleAsync(delivery, stoppingToken);
            }
            await tidying;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // Without this worker turns would be accepted and never answered: readiness must say so.
            health.Fault = ex.GetType().Name;
            logger.LogCritical(ex, "The chat turn worker stopped.");
            throw;
        }
    }

    /// <summary>After a restart: a turn that was being answered cannot be resumed and is failed; waiting ones are published again.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        var now = DateTime.UtcNow;
        await db.ChatTurns.Where(t => t.State == TurnStates.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.State, TurnStates.Failed)
                .SetProperty(t => t.Error, "Interrupted by a server restart.")
                .SetProperty(t => t.FinishedAt, now), ct);
        var waiting = await db.ChatTurns.AsNoTracking().Where(t => t.State == TurnStates.Queued)
            .OrderBy(t => t.CreatedAt).Select(t => new { t.Id, t.UserId }).ToListAsync(ct);
        foreach (var turn in waiting) await bus.PublishAsync(WorkQueues.ChatTurns, new WorkMessage(turn.Id, turn.UserId), ct);
    }

    /// <summary>Finished turns and their events are for catching up, not an archive: the conversation itself is in the session.</summary>
    private async Task TidyAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using (var scope = scopes.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
                    var before = DateTime.UtcNow - options.Retention;
                    var old = db.ChatTurns.Where(t => t.FinishedAt != null && t.FinishedAt < before);
                    await db.ChatTurnEvents.Where(e => old.Any(t => t.Id == e.TurnId)).ExecuteDeleteAsync(ct);
                    await old.ExecuteDeleteAsync(ct);
                }
                await Task.Delay(TimeSpan.FromMinutes(10), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tidying up old chat turns stopped.");
        }
    }

    private async Task HandleAsync(Delivery delivery, CancellationToken stoppingToken)
    {
        var message = delivery.Message;
        try
        {
            ChatTurn? turn;
            using (var scope = scopes.CreateScope())
            {
                turn = await scope.ServiceProvider.GetRequiredService<CerberusDbContext>().ChatTurns.AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == message.Id, stoppingToken);
            }
            if (turn is null || turn.State != TurnStates.Queued)
            {
                // Cancelled while it waited, or delivered a second time.
            }
            else if (turn.UserId != message.UserId)
            {
                // The message names another user than the turn belongs to: not something this service publishes.
                logger.LogWarning("A queue message for turn {TurnId} named a user the turn does not belong to; it was not answered.", turn.Id);
            }
            else
            {
                await InOrderAsync(turn, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;   // shutting down: not acknowledged, so a durable queue hands it out again
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Turn {TurnId} failed outside its reply.", message.Id);
        }
        await delivery.DoneAsync();
    }

    /// <summary>Waits for the conversation, then for one of the user's places, then for a place at all — and answers.</summary>
    private async Task InOrderAsync(ChatTurn turn, CancellationToken stoppingToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using (await _sessions.EnterAsync(turn.SessionId, 1, stoppingToken))
            {
                // Two turns of one conversation sent in quick succession may arrive here the wrong way round:
                // the later one steps back and lets the earlier one through. (Bounded, in case a message was lost.)
                if (attempt >= 100 || !await EarlierWaitingAsync(turn, stoppingToken))
                {
                    using (await _users.EnterAsync(turn.UserId, Math.Max(1, options.PerUser), stoppingToken))
                    {
                        await _places.WaitAsync(stoppingToken);
                        try
                        {
                            await AnswerAsync(turn.Id, stoppingToken);
                        }
                        finally
                        {
                            _places.Release();
                        }
                    }
                    return;
                }
            }
            await Task.Delay(50, stoppingToken);
        }
    }

    private async Task<bool> EarlierWaitingAsync(ChatTurn turn, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CerberusDbContext>().ChatTurns
            .AnyAsync(t => t.SessionId == turn.SessionId && t.Id != turn.Id && t.State == TurnStates.Queued && t.CreatedAt < turn.CreatedAt, ct);
    }

    private async Task AnswerAsync(string turnId, CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();

        // Claim it. Conditional on it still waiting, so a cancel — or another worker — wins cleanly.
        var startedAt = DateTime.UtcNow;
        var claimed = await db.ChatTurns.Where(t => t.Id == turnId && t.State == TurnStates.Queued)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.State, TurnStates.Running).SetProperty(t => t.StartedAt, startedAt), stoppingToken);
        if (claimed != 1) return;
        var turn = await db.ChatTurns.AsNoTracking().FirstAsync(t => t.Id == turnId, stoppingToken);
        await TurnDelivery.PushAsync(hub, turn.UserId, ChatHub.ChatTurn, ChatTurnEndpoints.Describe(turn, null), logger);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        stop.CancelAfter(options.TurnTimeout);
        var delivery = new TurnDelivery(turn, hub, scopes, logger);
        var active = registry.Add(turn.Id, stop, delivery);
        var writer = new UiMessageStreamWriter(delivery.WriteAsync);

        var state = TurnStates.Completed;
        string? error = null;
        var stored = false;
        try
        {
            var body = JsonSerializer.Deserialize<UiChatRequest>(turn.Request, Json) ?? throw new JsonException("The stored request is empty.");
            // The user is the turn's — the one the API established when it accepted the request.
            var (problem, prepared) = await scope.ServiceProvider.GetRequiredService<ChatService>().PrepareAsync(turn.UserId, body, stop.Token);
            if (prepared is null)
            {
                // The conversation was removed, or the model withdrawn, between accepting and answering.
                (state, error) = (TurnStates.Failed, problem!.Error);
                await writer.ErrorAsync(problem.Error, CancellationToken.None);
            }
            else
            {
                var result = await scope.ServiceProvider.GetRequiredService<ChatService>().RunAsync(prepared, writer, stop.Token);
                stored = result.Complete;
                // The model or its provider gave up part-way; the reply itself says why.
                if (result.Failed) (state, error) = (TurnStates.Failed, "The model could not complete the reply.");
            }
        }
        catch (OperationCanceledException)
        {
            (state, error) = active.StoppedByUser ? (TurnStates.Cancelled, "Stopped by the user.")
                : stoppingToken.IsCancellationRequested ? (TurnStates.Failed, "Interrupted: the server is stopping.")
                : (TurnStates.Failed, $"It took longer than {options.TurnTimeout.TotalMinutes:0} minutes.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Turn {TurnId} failed.", turn.Id);
            (state, error) = (TurnStates.Failed, "The reply could not be completed.");
            await writer.ErrorAsync(error, CancellationToken.None);
        }
        finally
        {
            // Stored first, announced second: whoever hears that the turn is over can read all of it.
            await delivery.StoreAsync();
            var finishedAt = DateTime.UtcNow;
            var last = delivery.LastSequence;
            await db.ChatTurns.Where(t => t.Id == turn.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.State, state)
                    .SetProperty(t => t.Error, error)
                    .SetProperty(t => t.ReplyStored, stored)
                    .SetProperty(t => t.LastSequence, last)
                    .SetProperty(t => t.FinishedAt, finishedAt), CancellationToken.None);
            registry.Remove(turn.Id);
            var finished = await db.ChatTurns.AsNoTracking().FirstAsync(t => t.Id == turn.Id, CancellationToken.None);
            await TurnDelivery.PushAsync(hub, turn.UserId, ChatHub.ChatTurn, ChatTurnEndpoints.Describe(finished, null), logger);
        }
    }
}

/// <summary>
/// A conversation over the queue: a request is accepted and queued under the signed-in user's id, a worker
/// answers it, and the reply comes over SignalR (<c>/hub/chat</c>) — and from here, by sequence number, for a
/// client that was not connected. Everything is found under the caller's own id only.
/// </summary>
public static class ChatTurnEndpoints
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void MapChatTurnEndpoints(this WebApplication app)
    {
        var turns = app.MapGroup("/chat/turns").AddEndpointFilter<RequireAuthFilter>();
        turns.MapPost("/", Submit);
        turns.MapGet("/", List);
        turns.MapGet("/{id}", Get);
        turns.MapGet("/{id}/events", Events);
        turns.MapPost("/{id}/cancel", Cancel);
    }

    private static async Task<IResult> Submit(
        UiChatRequest body, HttpContext http, CerberusDbContext db, IMessageBus bus, ChatTurnOptions options, CancellationToken ct)
    {
        // The user is the one the token belongs to. Nothing in the body can name another.
        var userId = UserId(http);
        if (ChatService.Check(body) is { } problem) return Results.Json(new { error = problem.Error }, statusCode: problem.Status);
        if (!await db.Sessions.AnyAsync(s => s.Id == body.Id && s.UserId == userId, ct))
        {
            return Results.Json(new { error = "Session not found" }, statusCode: 404);
        }

        var ahead = await db.ChatTurns.CountAsync(t => t.UserId == userId && TurnStates.Active.Contains(t.State), ct);
        if (ahead >= options.MaxWaitingPerUser)
        {
            return Results.Json(new { error = $"Too many requests are waiting ({ahead}). Let some finish first." }, statusCode: 429);
        }

        var turn = new ChatTurn { UserId = userId, SessionId = body.Id!, Request = JsonSerializer.Serialize(body, Json) };
        db.ChatTurns.Add(turn);
        await db.SaveChangesAsync(ct);
        await bus.PublishAsync(WorkQueues.ChatTurns, new WorkMessage(turn.Id, userId), ct);
        return Results.Json(Describe(turn, ahead), statusCode: 202);
    }

    private static async Task<IResult> List(HttpContext http, CerberusDbContext db, string? sessionId, bool? active, CancellationToken ct)
    {
        var userId = UserId(http);
        var query = db.ChatTurns.AsNoTracking().Where(t => t.UserId == userId);
        if (!string.IsNullOrWhiteSpace(sessionId)) query = query.Where(t => t.SessionId == sessionId);
        if (active == true) query = query.Where(t => TurnStates.Active.Contains(t.State));
        var turns = await query.OrderByDescending(t => t.CreatedAt).Take(50).ToListAsync(ct);
        return Results.Json(turns.Select(t => Describe(t, null)));
    }

    private static async Task<IResult> Get(string id, HttpContext http, CerberusDbContext db, TurnRegistry registry, CancellationToken ct)
    {
        var userId = UserId(http);
        var turn = await db.ChatTurns.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
        return turn is null ? NotFound() : Results.Json(Describe(turn, null, registry.Find(id)?.Delivery.LastSequence));
    }

    private static async Task<IResult> Events(
        string id, HttpContext http, CerberusDbContext db, TurnRegistry registry, long? after, int? limit, CancellationToken ct)
    {
        var userId = UserId(http);
        var turn = await db.ChatTurns.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
        if (turn is null) return NotFound();

        var from = Math.Max(0, after ?? 0);
        var take = Math.Clamp(limit ?? 500, 1, 2000);
        // What is being written right now is asked for first, the stored part second: an event that moves from
        // the one to the other in between is then seen twice (and dropped), never missed.
        var live = registry.Find(id)?.Delivery;
        var fresh = live?.Unstored(from) ?? [];
        var kept = await db.ChatTurnEvents.AsNoTracking()
            .Where(e => e.TurnId == id && e.Sequence > from).OrderBy(e => e.Sequence).Take(take).ToListAsync(ct);
        var events = kept.Concat(fresh.Where(f => kept.All(k => k.Sequence != f.Sequence))).OrderBy(e => e.Sequence).Take(take).ToList();
        var lastSequence = Math.Max(live?.LastSequence ?? 0, Math.Max(turn.LastSequence, events.Count > 0 ? events[^1].Sequence : 0));

        return Results.Json(new
        {
            turnId = turn.Id,
            sessionId = turn.SessionId,
            state = turn.State,
            error = turn.Error,
            replyStored = turn.ReplyStored,
            lastSequence,
            events = events.Select(e => new { sequence = e.Sequence, chunk = JsonSerializer.Deserialize<JsonElement>(e.Chunk) }),
            // True when this page did not reach the end: ask again with after = the last sequence returned.
            more = events.Count > 0 && events[^1].Sequence < lastSequence,
        });
    }

    private static async Task<IResult> Cancel(
        string id, HttpContext http, CerberusDbContext db, TurnRegistry registry, IHubContext<ChatHub> hub, ILoggerFactory loggers, CancellationToken ct)
    {
        var userId = UserId(http);
        var turn = await db.ChatTurns.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
        if (turn is null) return NotFound();

        // Still waiting: it simply never starts. Being answered here: its reply is stopped, and the worker records how it ended.
        var now = DateTime.UtcNow;
        var withdrawn = await db.ChatTurns.Where(t => t.Id == id && t.State == TurnStates.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.State, TurnStates.Cancelled)
                .SetProperty(t => t.Error, "Stopped by the user.")
                .SetProperty(t => t.FinishedAt, now), ct);
        if (withdrawn == 1)
        {
            var cancelled = await db.ChatTurns.AsNoTracking().FirstAsync(t => t.Id == id, ct);
            await TurnDelivery.PushAsync(hub, userId, ChatHub.ChatTurn, Describe(cancelled, null), loggers.CreateLogger("Chat"));
            return Results.Json(Describe(cancelled, null));
        }
        var stopping = registry.TryStop(id);
        var current = await db.ChatTurns.AsNoTracking().FirstAsync(t => t.Id == id, ct);
        return Results.Json(Describe(current, null), statusCode: stopping ? 202 : 200);
    }

    /// <param name="ahead">On accepting: how many of the user's own turns are before this one.</param>
    internal static object Describe(ChatTurn t, int? ahead, long? liveSequence = null) => new
    {
        turnId = t.Id,
        sessionId = t.SessionId,
        state = t.State,
        error = t.Error,
        replyStored = t.ReplyStored,
        lastSequence = Math.Max(t.LastSequence, liveSequence ?? 0),
        ahead,
        createdAt = t.CreatedAt,
        startedAt = t.StartedAt,
        finishedAt = t.FinishedAt,
    };

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static IResult NotFound() => Results.Json(new { error = "Turn not found" }, statusCode: 404);
}
