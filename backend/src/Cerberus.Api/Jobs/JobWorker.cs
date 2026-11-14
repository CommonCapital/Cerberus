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
    JobOptions options, JobEvents events, RunStream stream, OperatorDesk desk, ILogger<JobWorker> logger,
    Cerberus.Api.Health.WorkerHealth health, Cluster.InstanceIdentity instance, Cluster.OrphanSweeper orphans) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverAsync(stoppingToken);

            var consumers = Enumerable.Range(0, options.Concurrency)
                .Select(_ => ConsumeAsync(stoppingToken))
                .ToArray();
            health.StartedAt = DateTime.UtcNow;
            await Task.WhenAll(consumers);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Without this worker jobs would be accepted and never run: readiness must say so.
            health.Fault = ex.GetType().Name;
            logger.LogCritical(ex, "The job worker stopped.");
            throw;
        }
    }

    /// <summary>
    /// At start: jobs that were running under an instance that is gone cannot be resumed and are failed —
    /// those a living instance is running are left alone (see <see cref="Cluster.OrphanSweeper"/>). Jobs still
    /// queued are published again.
    /// </summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        await orphans.BeatAsync(ct);
        var (interrupted, _) = await orphans.SweepAsync(ct);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();

        // Published again whatever the queue: a durable queue may still hold them, and a second message
        // for a job is harmless — a job that is no longer queued is not started.
        var queued = await db.Jobs.AsNoTracking()
            .Where(j => j.State == nameof(JobState.Queued))
            .OrderBy(j => j.CreatedAt)
            .Select(j => new { j.Id, j.UserId })
            .ToListAsync(ct);
        foreach (var job in queued) await bus.PublishAsync(WorkQueues.Jobs, new WorkMessage(job.Id, job.UserId), ct);

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
            await foreach (var delivery in bus.ConsumeAsync(WorkQueues.Jobs, stoppingToken))
            {
                var message = delivery.Message;
                try
                {
                    await ProcessAsync(message, stoppingToken);
                    // Only now: the job has reached a final state, or was never this message's to start.
                    await delivery.DoneAsync();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;   // shutting down: not acknowledged, so a durable queue hands it out again
                }
                catch (Exception ex)
                {
                    // One broken job must not take the consumer down, nor come back for ever.
                    logger.LogError(ex, "Job {JobId} failed outside its executor.", message.Id);
                    await delivery.DoneAsync();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task ProcessAsync(WorkMessage message, CancellationToken stoppingToken)
    {
        var jobId = message.Id;
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<CerberusDbContext>();
        var audit = services.GetRequiredService<AuditLog>();

        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, stoppingToken);
        if (job is null || job.State != nameof(JobState.Queued)) return;   // cancelled while waiting, or delivered twice
        if (job.UserId != message.UserId)
        {
            // The message names another user than the job belongs to: not something this service publishes.
            logger.LogWarning("A queue message for job {JobId} named a user the job does not belong to; it was not run.", jobId);
            return;
        }

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
                .SetProperty(j => j.OwnerId, instance.Id)
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

            case JobTypes.Sast:
                var sast = JsonSerializer.Deserialize<Agents.SastJobParameters>(job.Parameters, JobTypes.Json)
                    ?? throw new InvalidOperationException("The job has no parameters.");
                return (await services.GetRequiredService<Agents.SastAgentRunner>().RunAsync(job, sast, reporter, ct)).Id;

            case JobTypes.Index:
                return (await services.GetRequiredService<Search.CodeIndexer>().BuildAsync(job, reporter, ct)).Id;

            case JobTypes.ThreatModel:
                var threatModel = JsonSerializer.Deserialize<Agents.ThreatModelJobParameters>(job.Parameters, JobTypes.Json)
                    ?? throw new InvalidOperationException("The job has no parameters.");
                return (await services.GetRequiredService<Agents.ThreatModelAgentRunner>().RunAsync(job, threatModel, reporter, ct)).Id;

            case JobTypes.Investigate:
                var investigate = JsonSerializer.Deserialize<Agents.InvestigateJobParameters>(job.Parameters, JobTypes.Json)
                    ?? throw new InvalidOperationException("The job has no parameters.");
                return (await services.GetRequiredService<Agents.LeadAgentRunner>().RunAsync(job, investigate, reporter, ct)).Id;

            case JobTypes.Evidence:
                var evidence = JsonSerializer.Deserialize<Agents.EvidenceJobParameters>(job.Parameters, JobTypes.Json)
                    ?? throw new InvalidOperationException("The job has no parameters.");
                await services.GetRequiredService<Agents.EvidenceAgentRunner>().RunAsync(job, evidence, reporter, ct);
                return evidence.AssessmentId;

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

    /// <summary>The SAST agent: a model reads the code and reports vulnerabilities. The result id is the agent run.</summary>
    public const string Sast = "sast";

    /// <summary>The evidence agent: a model suggests answers for controls the engine could not judge. The result id is the assessment.</summary>
    public const string Evidence = "evidence";

    /// <summary>The threat-model agent: a model maps components and threats and writes them up. The result id is the agent run.</summary>
    public const string ThreatModel = "threat-model";

    /// <summary>Builds or refreshes the semantic index of a target with a local embedding model. The result id is the index.</summary>
    public const string Index = "index";

    /// <summary>A team review: a lead agent plans, dispatches specialist agents and merges what they find. The result id is the lead's agent run.</summary>
    public const string Investigate = "investigate";

    public static readonly IReadOnlyList<string> All = [Scan, Assess, Triage, Review, Sast, Evidence, ThreatModel, Index, Investigate];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
