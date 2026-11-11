using System.Text.Json;
using Cerberus.Api.Audit;
using Cerberus.Authorization;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Jobs;

public sealed record ScanJobParameters(bool Sca = true, string Exclusions = ScanExclusions.Ask);

public sealed record AssessJobParameters(string CatalogId, string? ScanId, bool Sca = true);

/// <summary>
/// Executes queued jobs. Runs <see cref="JobOptions.Concurrency"/> consumers
/// over the message bus; each job gets its own DI scope, is re-authorized at
/// the moment it starts (scope may have changed while it waited), and runs
/// under <see cref="JobExecutor"/>'s cancel / time-limit / hard-stop controls.
/// </summary>
public sealed class JobWorker(
    IMessageBus bus, IServiceScopeFactory scopes, JobCancellationRegistry registry,
    JobOptions options, JobEvents events, RunStream stream, OperatorDesk desk, ILogger<JobWorker> logger) : BackgroundService
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
            .Where(j => j.State == nameof(JobState.Running) || j.State == nameof(JobState.AwaitingInput))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, nameof(JobState.Failed))
                .SetProperty(j => j.Error, "Interrupted by a server restart.")
                .SetProperty(j => j.FinishedAt, now), ct);

        // Questions of jobs that died with the old process can no longer be answered.
        await db.OperatorQuestions
            .Where(q => q.Status == QuestionStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.Status, QuestionStatus.Cancelled)
                .SetProperty(q => q.ResolvedAt, now), ct);

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

        // A job that needed the operator's consent runs only if it was given.
        if (job.ApprovalId is not null)
        {
            var status = await db.ApprovalRequests.AsNoTracking()
                .Where(a => a.Id == job.ApprovalId && a.JobId == job.Id)
                .Select(a => a.Status)
                .FirstOrDefaultAsync(stoppingToken);
            if (status != ApprovalStatus.Approved)
            {
                await FinishAsync(db, audit, job, nameof(JobState.Queued),
                    new JobOutcome(JobState.Failed, null, "Not approved: the job's approval request is missing or was not approved."));
                return;
            }
        }

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
        await events.PublishAsync(jobId);

        var outcome = await JobExecutor.RunAsync(
            jobId,
            (ct, timeLimit) => RunWorkAsync(
                services, job, stream.For(job, (question, askCt) => desk.AskAsync(job, timeLimit, question, askCt)), ct),
            registry, options.Timeout, options.Grace, stoppingToken);

        await FinishAsync(db, audit, job, nameof(JobState.Running), outcome);
    }

    /// <summary>Dispatches on job type and returns the id of what was produced.</summary>
    private static async Task<string?> RunWorkAsync(
        IServiceProvider services, Job job, IRunReporter reporter, CancellationToken ct)
    {
        switch (job.Type)
        {
            case JobTypes.Scan:
                var parameters = JsonSerializer.Deserialize<ScanJobParameters>(job.Parameters, JobTypes.Json) ?? new();
                var run = await services.GetRequiredService<ScanRunner>()
                    .RunAsync(job.UserId, job.EngagementId, job.TargetPath, parameters.Sca, ct, reporter, parameters.Exclusions);
                return run.Scan.Id;

            case JobTypes.Assess:
                var assess = JsonSerializer.Deserialize<AssessJobParameters>(job.Parameters, JobTypes.Json)
                    ?? throw new InvalidOperationException("The job has no parameters.");
                var catalog = services.GetRequiredService<Compliance.CatalogLoader>().Get(assess.CatalogId)
                    ?? throw new InvalidOperationException($"Catalog '{assess.CatalogId}' is no longer installed.");

                Scan? scan = null;
                if (assess.ScanId is not null)
                {
                    var db = services.GetRequiredService<CerberusDbContext>();
                    scan = await db.Scans.AsNoTracking()
                        .FirstOrDefaultAsync(s => s.Id == assess.ScanId && s.UserId == job.UserId, ct)
                        ?? throw new InvalidOperationException($"Scan {assess.ScanId} no longer exists.");
                }
                var assessment = await services.GetRequiredService<AssessmentRunner>()
                    .RunAsync(job.UserId, job.EngagementId, catalog, scan, job.TargetPath, assess.Sca, ct, reporter);
                return assessment.Assessment.Id;

            case JobTypes.Triage:
                var triage = JsonSerializer.Deserialize<TriageJobParameters>(job.Parameters, JobTypes.Json)
                    ?? throw new InvalidOperationException("The job has no parameters.");
                await services.GetRequiredService<TriageRunner>().RunAsync(job.UserId, triage, ct, reporter);
                return triage.ScanId;

            case JobTypes.Review:
                var review = JsonSerializer.Deserialize<ReviewJobParameters>(job.Parameters, JobTypes.Json)
                    ?? throw new InvalidOperationException("The job has no parameters.");
                var outcome = await services.GetRequiredService<ReviewRunner>().RunAsync(job, review, reporter, ct);
                return outcome.Review.Id;

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

        // A job that started may end while it is waiting for the operator.
        var alsoFrom = fromState == nameof(JobState.Running) ? nameof(JobState.AwaitingInput) : fromState;
        await db.Jobs
            .Where(j => j.Id == job.Id && (j.State == fromState || j.State == alsoFrom))
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

        await events.PublishAsync(job.Id);

        if (outcome.Abandoned)
        {
            logger.LogWarning("Job {JobId} ignored its stop signal and was abandoned.", job.Id);
        }
    }
}

public static class JobTypes
{
    public const string Scan = "scan";

    /// <summary>A compliance assessment; the result id is the assessment.</summary>
    public const string Assess = "assess";

    /// <summary>LLM triage of a stored scan's findings; the result id is the scan.</summary>
    public const string Triage = "triage";

    /// <summary>A planned, multi-step review: scan, optional triage, assessments, report. The result id is the review.</summary>
    public const string Review = "review";

    public static readonly IReadOnlyList<string> All = [Scan, Assess, Triage, Review];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
