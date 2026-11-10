using Cerberus.Api.Audit;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Jobs;

public sealed record CancelSummary(int Queued, int Running);

public sealed record JobOptions(int Concurrency, TimeSpan Timeout, TimeSpan Grace, int MaxQueuedPerUser);

/// <summary>
/// The operator's controls over jobs: cancel one, or stop everything an
/// engagement has queued or running (the kill switch).
/// </summary>
public sealed class JobControl(CerberusDbContext db, JobCancellationRegistry registry, AuditLog audit, JobEvents events)
{
    public enum CancelResult { Cancelled, StopRequested, AlreadyFinished }

    public async Task<CancelResult> CancelAsync(Job job, CancellationToken ct)
    {
        // A queued job is cancelled outright. The conditional update makes this
        // atomic against a worker claiming the job at the same moment.
        var now = DateTime.UtcNow;
        var cancelledQueued = await db.Jobs
            .Where(j => j.Id == job.Id && j.State == nameof(JobState.Queued))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, nameof(JobState.Cancelled))
                .SetProperty(j => j.Error, "Cancelled by the operator before it started.")
                .SetProperty(j => j.FinishedAt, now), ct);
        if (cancelledQueued == 1)
        {
            await audit.AppendAsync(job.UserId, "job.cancel", AuditOutcome.Ok,
                $"Job {job.Id} cancelled before it started.", job.EngagementId, job.TargetPath, ct: ct);
            await events.PublishAsync(job.Id);
            return CancelResult.Cancelled;
        }

        // A running job is signalled; the worker records the final state.
        if (registry.Cancel(job.Id))
        {
            await audit.AppendAsync(job.UserId, "job.cancel", AuditOutcome.Ok,
                $"Stop requested for running job {job.Id}.", job.EngagementId, job.TargetPath, ct: ct);
            return CancelResult.StopRequested;
        }
        return CancelResult.AlreadyFinished;
    }

    public async Task<CancelSummary> CancelEngagementAsync(string userId, string engagementId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var queuedIds = await db.Jobs.AsNoTracking()
            .Where(j => j.UserId == userId && j.EngagementId == engagementId && j.State == nameof(JobState.Queued))
            .Select(j => j.Id)
            .ToListAsync(ct);
        var queued = await db.Jobs
            .Where(j => queuedIds.Contains(j.Id) && j.State == nameof(JobState.Queued))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, nameof(JobState.Cancelled))
                .SetProperty(j => j.Error, "Cancelled: the engagement was aborted.")
                .SetProperty(j => j.FinishedAt, now), ct);

        var runningIds = await db.Jobs.AsNoTracking()
            .Where(j => j.UserId == userId && j.EngagementId == engagementId && j.State == nameof(JobState.Running))
            .Select(j => j.Id)
            .ToListAsync(ct);
        var running = runningIds.Count(registry.Cancel);

        if (queued + running > 0)
        {
            await audit.AppendAsync(userId, "engagement.abort.jobs", AuditOutcome.Ok,
                $"Kill switch: cancelled {queued} queued job(s) and requested stop of {running} running job(s).",
                engagementId, ct: ct);
        }
        foreach (var id in queuedIds) await events.PublishAsync(id);
        return new CancelSummary(queued, running);
    }
}
