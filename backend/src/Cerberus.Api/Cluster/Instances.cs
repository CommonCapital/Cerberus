using Cerberus.Api.Chat;
using Cerberus.Api.Hubs;
using Cerberus.Api.Jobs;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Cluster;

/// <summary>Who this running copy of the service is. New with every start: a restarted process is a different instance.</summary>
public sealed class InstanceIdentity
{
    public string Id { get; } = Guid.NewGuid().ToString("n");
}

/// <summary>
/// Which running work still has somebody running it (PLAN.md §22 item 7).
/// <para>
/// Every instance of the service says "I am here" in the shared database every few seconds, and puts its
/// name on each job and chat turn it starts. Work that is marked as running under a name that has gone
/// quiet — the process was stopped, crashed, or its machine went away — will never finish, and is failed
/// with a plain reason, by whichever instance notices first. Work of an instance that is alive is left
/// alone: starting a second copy of the service no longer fails what the first is doing.
/// </para>
/// An instance that stops in an orderly way takes its name off the list, so what it left behind is failed
/// at the next start without waiting. This needs nothing but the database, so it also holds with one instance.
/// </summary>
public sealed class OrphanSweeper(
    IServiceScopeFactory scopes, InstanceIdentity me, JobEvents events, IHubContext<ChatHub> hub, TimeProvider clock, ILogger<OrphanSweeper> logger)
{
    /// <summary>How often an instance says it is alive.</summary>
    public static readonly TimeSpan Beat = TimeSpan.FromSeconds(10);

    /// <summary>How long an instance may stay quiet before its work is taken to be nobody's.</summary>
    public static readonly TimeSpan QuietFor = TimeSpan.FromSeconds(45);

    public const string Interrupted = "Interrupted by a server restart.";

    private readonly DateTime _startedAt = clock.GetUtcNow().UtcDateTime;

    private readonly SemaphoreSlim _beating = new(1, 1);

    /// <summary>"I am here." Said by the heartbeat, and by each worker before it looks for orphans at start.</summary>
    public async Task BeatAsync(CancellationToken ct)
    {
        // One at a time: the first beat adds this instance to the list, and two firsts would both try to.
        await _beating.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
            var now = clock.GetUtcNow().UtcDateTime;
            var updated = await db.WorkerInstances.Where(i => i.Id == me.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.LastSeenAt, now), ct);
            if (updated == 0)
            {
                db.WorkerInstances.Add(new WorkerInstance { Id = me.Id, StartedAt = _startedAt, LastSeenAt = now });
                await db.SaveChangesAsync(ct);
            }
        }
        finally
        {
            _beating.Release();
        }
    }

    /// <summary>Takes this instance off the list: what it was running is nobody's from now on.</summary>
    public async Task LeaveAsync()
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
            await db.WorkerInstances.Where(i => i.Id == me.Id).ExecuteDeleteAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Leaving is a courtesy; an instance that could not say goodbye is noticed by its silence.
            logger.LogWarning("Could not take this instance off the list ({Problem}).", ex.GetType().Name);
        }
    }

    /// <summary>How many instances have said they are alive lately, this one included.</summary>
    public async Task<int> AliveAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        return (await AliveAsync(scope.ServiceProvider.GetRequiredService<CerberusDbContext>(), ct)).Count;
    }

    private async Task<List<string>> AliveAsync(CerberusDbContext db, CancellationToken ct)
    {
        var since = clock.GetUtcNow().UtcDateTime - QuietFor;
        var alive = await db.WorkerInstances.AsNoTracking().Where(i => i.LastSeenAt >= since).Select(i => i.Id).ToListAsync(ct);
        if (!alive.Contains(me.Id)) alive.Add(me.Id);   // whatever the list says, this instance knows it is here
        return alive;
    }

    /// <summary>Fails the running jobs and turns that nobody is running any more. Returns how many of each.</summary>
    public async Task<(int Jobs, int Turns)> SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        var alive = await AliveAsync(db, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        var jobIds = await db.Jobs.AsNoTracking()
            .Where(j => (j.State == nameof(JobState.Running) || j.State == nameof(JobState.AwaitingInput)) &&
                        (j.OwnerId == null || !alive.Contains(j.OwnerId)))
            .Select(j => j.Id).ToListAsync(ct);
        if (jobIds.Count > 0)
        {
            // Conditional again: a job that finished in between keeps its real ending.
            await db.Jobs
                .Where(j => jobIds.Contains(j.Id) && (j.State == nameof(JobState.Running) || j.State == nameof(JobState.AwaitingInput)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.State, nameof(JobState.Failed))
                    .SetProperty(j => j.Error, Interrupted)
                    .SetProperty(j => j.FinishedAt, now), ct);
            // Their questions can no longer be answered.
            await db.OperatorQuestions
                .Where(q => jobIds.Contains(q.JobId) && q.Status == QuestionStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(q => q.Status, QuestionStatus.Cancelled)
                    .SetProperty(q => q.ResolvedAt, now), ct);
        }

        var turns = await db.ChatTurns.AsNoTracking()
            .Where(t => t.State == TurnStates.Running && (t.OwnerId == null || !alive.Contains(t.OwnerId)))
            .ToListAsync(ct);
        if (turns.Count > 0)
        {
            var turnIds = turns.Select(t => t.Id).ToList();
            await db.ChatTurns.Where(t => turnIds.Contains(t.Id) && t.State == TurnStates.Running)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.State, TurnStates.Failed)
                    .SetProperty(t => t.Error, Interrupted)
                    .SetProperty(t => t.FinishedAt, now), ct);
        }

        // Names that went quiet a while ago are of no further use.
        var forgotten = now - TimeSpan.FromHours(1);
        await db.WorkerInstances.Where(i => i.LastSeenAt < forgotten && i.Id != me.Id).ExecuteDeleteAsync(ct);

        // Told to whoever is watching: a run that will never end must not look as if it were still going.
        foreach (var id in jobIds) await events.PublishAsync(id);
        foreach (var turn in turns)
        {
            var failed = await db.ChatTurns.AsNoTracking().FirstOrDefaultAsync(t => t.Id == turn.Id, ct);
            if (failed is not null) await TurnDelivery.PushAsync(hub, failed.UserId, ChatHub.ChatTurn, ChatTurnEndpoints.Describe(failed, null), logger);
        }
        if (jobIds.Count + turns.Count > 0)
        {
            logger.LogInformation("Work nobody was running any more: {Jobs} job(s) and {Turns} chat turn(s) failed.", jobIds.Count, turns.Count);
        }
        return (jobIds.Count, turns.Count);
    }
}

/// <summary>Keeps saying that this instance is alive, looks for orphaned work now and then, and says goodbye on an orderly stop.</summary>
public sealed class InstanceHeartbeat(OrphanSweeper sweeper, ILogger<InstanceHeartbeat> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var beat = 0; !stoppingToken.IsCancellationRequested; beat++)
        {
            try
            {
                await sweeper.BeatAsync(stoppingToken);
                if (beat % 3 == 0) await sweeper.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The database was busy or away for a moment: the next beat tries again.
                logger.LogWarning("A heartbeat was missed ({Problem}).", ex.GetType().Name);
            }
            try
            {
                await Task.Delay(OrphanSweeper.Beat, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await sweeper.LeaveAsync();
    }
}
