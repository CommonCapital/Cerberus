using System.Text.Json;
using Cerberus.Api.Audit;
using Cerberus.Authorization;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Jobs;

public sealed record ScanJobParameters(bool Sca = true);

/// <summary>
/// Executes queued jobs. Runs <see cref="JobOptions.Concurrency"/> consumers
/// over the message bus; each job gets its own DI scope, is re-authorized at
/// the moment it starts (scope may have changed while it waited), and runs
/// under <see cref="JobExecutor"/>'s cancel / time-limit / hard-stop controls.
/// </summary>
public sealed class JobWorker(
    IMessageBus bus, IServiceScopeFactory scopes, JobCancellationRegistry registry,
    JobOptions options, ILogger<JobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        var consumers = Enumerable.Range(0, options.Concurrency)
            .Select(_ => ConsumeAsync(stoppingToken))
            .ToArray();
        await Task.WhenAll(consumers);
    }

    /// <summary>
    /// After a restart the in-memory queue is gone: jobs that were running
    /// cannot be resumed and are failed; jobs still queued are re-published.
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();

        var now = DateTime.UtcNow;
        var interrupted = await db.Jobs
            .Where(j => j.State == nameof(JobState.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, nameof(JobState.Failed))
                .SetProperty(j => j.Error, "Interrupted by a server restart.")
                .SetProperty(j => j.FinishedAt, now), ct);

        var queued = await db.Jobs.AsNoTracking()
            .Where(j => j.State == nameof(JobState.Queued))
            .OrderBy(j => j.CreatedAt)
            .Select(j => j.Id)
            .ToListAsync(ct);
        foreach (var id in queued) await bus.PublishAsync(new JobMessage(id), ct);

        if (interrupted + queued.Count > 0)
        {
            logger.LogInformation("Job recovery: {Interrupted} interrupted job(s) failed, {Queued} queued job(s) re-published.",
                interrupted, queued.Count);
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in bus.ConsumeAsync(stoppingToken))
            {
                try
                {
                    await ProcessAsync(message.JobId, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // One broken job must not take the consumer down.
                    logger.LogError(ex, "Job {JobId} failed outside its executor.", message.JobId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task ProcessAsync(string jobId, CancellationToken stoppingToken)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<CerberusDbContext>();
        var audit = services.GetRequiredService<AuditLog>();

        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, stoppingToken);
        if (job is null || job.State != nameof(JobState.Queued)) return;   // cancelled while waiting

        // Authorize again at execution time: the engagement may have been
        // aborted, closed or re-scoped since the job was accepted.
        if (job.EngagementId is not null)
        {
            var tier = Enum.TryParse<CapabilityTier>(job.Tier, out var t) ? t : CapabilityTier.T3;
            var decision = await EngagementEndpoints.CheckScopeAsync(
                db, audit, job.UserId, job.EngagementId, "job.start", tier, job.TargetPath, stoppingToken);
            if (decision is not { Allowed: true })
            {
                await FinishAsync(db, audit, job, nameof(JobState.Queued), new JobOutcome(JobState.Failed, null,
                    $"No longer authorized: {decision?.Reason ?? "the engagement no longer exists."}"));
                return;
            }
        }

        // Claim the job. Conditional on it still being queued, so a concurrent cancel wins cleanly.
        var startedAt = DateTime.UtcNow;
        var claimed = await db.Jobs
            .Where(j => j.Id == jobId && j.State == nameof(JobState.Queued))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, nameof(JobState.Running))
                .SetProperty(j => j.StartedAt, startedAt), stoppingToken);
        if (claimed != 1) return;

        var outcome = await JobExecutor.RunAsync(
            jobId, ct => RunWorkAsync(services, job, ct), registry, options.Timeout, options.Grace, stoppingToken);

        await FinishAsync(db, audit, job, nameof(JobState.Running), outcome);
    }

    /// <summary>Dispatches on job type and returns the id of what was produced.</summary>
    private static async Task<string?> RunWorkAsync(IServiceProvider services, Job job, CancellationToken ct)
    {
        switch (job.Type)
        {
            case JobTypes.Scan:
                var parameters = JsonSerializer.Deserialize<ScanJobParameters>(job.Parameters, JobTypes.Json) ?? new();
                var run = await services.GetRequiredService<ScanRunner>()
                    .RunAsync(job.UserId, job.EngagementId, job.TargetPath, parameters.Sca, ct);
                return run.Scan.Id;
            default:
                throw new InvalidOperationException($"Unknown job type \"{job.Type}\".");
        }
    }

    private async Task FinishAsync(CerberusDbContext db, AuditLog audit, Job job, string fromState, JobOutcome outcome)
    {
        // Final bookkeeping must happen even during shutdown, hence no cancellation token.
        var finishedAt = DateTime.UtcNow;
        var state = outcome.State.ToString();
        var error = outcome.Abandoned
            ? $"{outcome.Error} The work did not stop within the grace period and was abandoned."
            : outcome.Error;

        await db.Jobs
            .Where(j => j.Id == job.Id && j.State == fromState)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, state)
                .SetProperty(j => j.Error, error)
                .SetProperty(j => j.ResultId, outcome.ResultId)
                .SetProperty(j => j.FinishedAt, finishedAt));

        await audit.AppendAsync(job.UserId, "job.finish",
            outcome.State == JobState.Succeeded ? AuditOutcome.Ok : AuditOutcome.Error,
            $"Job {job.Id} ({job.Type}) {state.ToLowerInvariant()}" + (error is null ? "." : $": {error}"),
            job.EngagementId, job.TargetPath,
            Enum.TryParse<CapabilityTier>(job.Tier, out var tier) ? tier : null);

        if (outcome.Abandoned)
        {
            logger.LogWarning("Job {JobId} ignored its stop signal and was abandoned.", job.Id);
        }
    }
}

public static class JobTypes
{
    public const string Scan = "scan";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
