using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Jobs;

/// <param name="Ahead">How many of the user's own runs come before this one: those running, and those queued earlier.</param>
/// <param name="Others">How many other people have work waiting or running — the places are shared with them in turn.</param>
public sealed record QueuePosition(int Ahead, int Others);

/// <summary>
/// Whose turn it is (PLAN.md §22 item 4). The queue of messages only says that there is work; which job a
/// free worker takes is decided here, from the database, each time:
/// <list type="bullet">
/// <item>the next job belongs to the user who has the fewest running right now — so one person's batch of
/// twenty does not stand in front of another person's single run;</item>
/// <item>between users with equally few, the one whose last run started longest ago goes first — in turn,
/// round the table. (Not "whose oldest job is oldest": with one worker nobody has anything running at the
/// moment a place frees, so that rule would be first-come again.)</item>
/// <item>within one user's jobs, the oldest goes first, as before;</item>
/// <item>where a limit per user is set (CERBERUS_JOB_PER_USER), a user at the limit is passed over even if
/// workers are free. Without one, a single user may use every worker — nobody else is waiting for them.</item>
/// </list>
/// Because the count is taken from the database, it is the same for every instance of the service.
/// </summary>
public sealed class JobQueue(IServiceScopeFactory scopes)
{
    private static readonly string[] Busy = [nameof(JobState.Running), nameof(JobState.AwaitingInput)];

    /// <summary>The job a free worker should take now, or null when nothing may start.</summary>
    /// <param name="perUser">The most jobs one user may have running; 0 for no limit.</param>
    public static async Task<Job?> NextAsync(CerberusDbContext db, int perUser, CancellationToken ct)
    {
        var waiting = await db.Jobs.AsNoTracking()
            .Where(j => j.State == nameof(JobState.Queued))
            .GroupBy(j => j.UserId)
            .Select(g => new { UserId = g.Key, Since = g.Min(j => j.CreatedAt) })
            .ToListAsync(ct);
        if (waiting.Count == 0) return null;

        // A job that waits for its operator's answer still holds its worker: it counts as running.
        var running = await db.Jobs.AsNoTracking()
            .Where(j => Busy.Contains(j.State))
            .GroupBy(j => j.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.UserId, g => g.Count, ct);

        // When each of them last got a place. Somebody who never has comes first.
        var users = waiting.Select(w => w.UserId).ToList();
        var lastStarted = await db.Jobs.AsNoTracking()
            .Where(j => users.Contains(j.UserId) && j.StartedAt != null)
            .GroupBy(j => j.UserId)
            .Select(g => new { UserId = g.Key, At = g.Max(j => j.StartedAt) })
            .ToDictionaryAsync(g => g.UserId, g => g.At, ct);

        var next = waiting
            .Where(w => perUser <= 0 || running.GetValueOrDefault(w.UserId) < perUser)
            .OrderBy(w => running.GetValueOrDefault(w.UserId))
            .ThenBy(w => lastStarted.GetValueOrDefault(w.UserId) ?? DateTime.MinValue)
            .ThenBy(w => w.Since)
            .FirstOrDefault();
        if (next is null) return null;

        return await db.Jobs.AsNoTracking()
            .Where(j => j.UserId == next.UserId && j.State == nameof(JobState.Queued))
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Where a waiting job stands, for its owner. Counted in a scope of its own: the request's view of the
    /// data shows one user's rows only, and "how many others are waiting" is a number about the rest —
    /// a number, and nothing else of theirs.
    /// </summary>
    public async Task<QueuePosition> PositionAsync(Job job, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        var ahead = await db.Jobs.AsNoTracking().CountAsync(j => j.UserId == job.UserId && j.Id != job.Id &&
            (Busy.Contains(j.State) || (j.State == nameof(JobState.Queued) && j.CreatedAt < job.CreatedAt)), ct);
        var others = await db.Jobs.AsNoTracking()
            .Where(j => j.UserId != job.UserId && (Busy.Contains(j.State) || j.State == nameof(JobState.Queued)))
            .Select(j => j.UserId).Distinct().CountAsync(ct);
        return new QueuePosition(ahead, others);
    }
}
