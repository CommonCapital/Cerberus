using System.Text.Json;
using System.Text.Json.Serialization;
using Cerberus.Api.Audit;
using Cerberus.Api.Auth;
using Cerberus.Authorization;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Jobs;

public sealed record SubmitJobRequest(
    string? Type, string? Path, bool? Sca, string? EngagementId, string? CatalogId, string? ScanId,
    string? Model, int? MaxFindings, bool? IncludeTestsAndDocs, string? Exclusions);

/// <summary>Background jobs (PLAN.md M3): submit, inspect, cancel.</summary>
public static class JobEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void MapJobEndpoints(this WebApplication app)
    {
        var jobs = app.MapGroup("/jobs").AddEndpointFilter<RequireAuthFilter>();
        jobs.MapPost("/", Submit);
        jobs.MapGet("/", List);
        jobs.MapGet("/{id}", Get);
        jobs.MapPost("/{id}/cancel", Cancel);
        jobs.MapGet("/{id}/events", Events);
    }

    private static async Task<IResult> Submit(
        SubmitJobRequest body, HttpContext http, CerberusDbContext db, AuditLog audit,
        IMessageBus bus, JobOptions options, Compliance.CatalogLoader catalogs, JobEvents events, RunPolicy policy, CancellationToken ct)
    {
        var userId = UserId(http);
        var type = string.IsNullOrWhiteSpace(body.Type) ? JobTypes.Scan : body.Type.Trim().ToLowerInvariant();
        if (!JobTypes.All.Contains(type))
        {
            return BadRequest($"Unknown job type \"{body.Type}\". Supported: {string.Join(", ", JobTypes.All)}");
        }

        var targetPath = string.IsNullOrWhiteSpace(body.Path) ? "." : body.Path!;
        var engagementId = string.IsNullOrWhiteSpace(body.EngagementId) ? null : body.EngagementId;
        string parameters;
        ApprovalNeed? approvalNeed = null;

        if (type == JobTypes.Triage)
        {
            if (string.IsNullOrWhiteSpace(body.ScanId)) return BadRequest("scanId is required for a triage job");
            // No default model: the caller decides where the code under review is sent.
            if (string.IsNullOrWhiteSpace(body.Model) || !Cerberus.Core.Models.ModelRegistry.IsSupported(body.Model))
            {
                return BadRequest("model is required and must be a supported model id (a local Ollama model keeps the code on this machine)");
            }
            if (body.MaxFindings is < 1 or > TriageRunner.MaxFindingsLimit)
            {
                return BadRequest($"maxFindings must be between 1 and {TriageRunner.MaxFindingsLimit}");
            }

            var scan = await db.Scans.AsNoTracking()
                .Where(s => s.Id == body.ScanId && s.UserId == userId)
                .Select(s => new { s.Id, s.TargetPath, s.EngagementId, s.Findings })
                .FirstOrDefaultAsync(ct);
            if (scan is null) return Results.Json(new { error = "Scan not found" }, statusCode: 404);
            if (engagementId is not null && engagementId != scan.EngagementId)
            {
                return BadRequest("That scan was not run under this engagement");
            }
            engagementId = scan.EngagementId;
            targetPath = scan.TargetPath;
            var triage = new TriageJobParameters(scan.Id, body.Model, body.MaxFindings ?? 15, body.IncludeTestsAndDocs ?? false);
            parameters = JsonSerializer.Serialize(triage, JobTypes.Json);

            // Code leaving this machine needs the operator's explicit consent, shown what exactly will go.
            var provider = Cerberus.Core.Models.ModelRegistry.Find(body.Model)!.Provider;
            if (!Cerberus.Triage.FindingTriager.IsLocal(provider))
            {
                var selected = TriageRunner.SelectFor(scan.Findings, triage);
                approvalNeed = new ApprovalNeed(
                    ApprovalKinds.SendCodeToCloudModel,
                    $"Send source code to {provider} ({body.Model}) for triage",
                    $"Triage of scan {scan.Id} (path \"{scan.TargetPath}\") would send the code around {selected.Count} finding(s) " +
                    $"in {selected.Select(f => f.File).Distinct().Count()} file(s) to {provider}, model {body.Model}: " +
                    $"about {2 * Cerberus.Triage.TriagePrompt.ContextLines + 1} lines per finding. " +
                    "Credential-shaped values are masked before sending; other code is sent as is. " +
                    "Nothing is sent until you approve.");
            }
        }
        else if (type == JobTypes.Assess)
        {
            var catalogId = string.IsNullOrWhiteSpace(body.CatalogId) ? "enterprise-sap" : body.CatalogId!;
            if (catalogs.Get(catalogId) is null) return BadRequest($"Unknown catalog '{catalogId}'");

            string? scanId = null;
            if (!string.IsNullOrWhiteSpace(body.ScanId))
            {
                var scan = await db.Scans.AsNoTracking()
                    .Where(s => s.Id == body.ScanId && s.UserId == userId)
                    .Select(s => new { s.Id, s.TargetPath, s.EngagementId })
                    .FirstOrDefaultAsync(ct);
                if (scan is null) return Results.Json(new { error = "Scan not found" }, statusCode: 404);

                // An assessment of a scan belongs to that scan's engagement.
                if (engagementId is not null && engagementId != scan.EngagementId)
                {
                    return BadRequest("That scan was not run under this engagement");
                }
                engagementId = scan.EngagementId;
                targetPath = scan.TargetPath;
                scanId = scan.Id;
            }
            parameters = JsonSerializer.Serialize(new AssessJobParameters(catalogId, scanId, body.Sca ?? true), JobTypes.Json);
        }
        else
        {
            // By default a scan job asks the operator if the target hides anything through its own markers.
            var exclusions = string.IsNullOrWhiteSpace(body.Exclusions) ? ScanExclusions.Ask : body.Exclusions.Trim().ToLowerInvariant();
            if (!ScanExclusions.IsValid(exclusions, allowAsk: true))
            {
                return BadRequest("exclusions must be ask, honour or ignore");
            }
            parameters = JsonSerializer.Serialize(new ScanJobParameters(body.Sca ?? true, exclusions), JobTypes.Json);
        }

        // Reject paths that cannot be valid up front instead of failing later in the worker.
        if (ScopePolicy.Normalize(TargetType.Path, targetPath) is null)
        {
            return BadRequest("path must be relative to the scan root, without '..'");
        }

        const CapabilityTier tier = CapabilityTier.T0;

        // Scope gate at submission; the worker checks again when the job starts.
        if (EngagementEndpoints.RequireEngagement(policy, engagementId) is { } required) return required;
        if (engagementId is not null &&
            await EngagementEndpoints.AuthorizeRunAsync(db, audit, userId, engagementId, "job.submit", tier, targetPath, ct) is { } denied)
        {
            return denied;
        }

        var waiting = await db.Jobs.CountAsync(j => j.UserId == userId && j.State == nameof(JobState.Queued), ct);
        if (waiting >= options.MaxQueuedPerUser)
        {
            return Results.Json(new { error = $"Too many queued jobs ({waiting}). Wait for some to finish or cancel them." }, statusCode: 429);
        }

        var job = new Job
        {
            UserId = userId,
            EngagementId = engagementId,
            Type = type,
            Tier = tier.ToString(),
            TargetPath = targetPath,
            Parameters = parameters,
        };
        if (approvalNeed is null)
        {
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct);
            await events.PublishAsync(job.Id);
            await bus.PublishAsync(new JobMessage(job.Id), ct);
            return Results.Json(Describe(job), JsonOptions, statusCode: 202);
        }

        // Human in the loop: store the job, but do not hand it to the workers yet.
        var approval = new ApprovalRequest
        {
            UserId = userId,
            JobId = job.Id,
            EngagementId = engagementId,
            Kind = approvalNeed.Kind,
            Title = approvalNeed.Title,
            Detail = approvalNeed.Detail,
        };
        job.State = nameof(JobState.AwaitingApproval);
        job.ApprovalId = approval.Id;
        db.Jobs.Add(job);
        db.ApprovalRequests.Add(approval);
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(userId, "approval.request", AuditOutcome.Ok,
            $"Job {job.Id} ({job.Type}) is waiting for approval: {approval.Title}.", engagementId, targetPath, tier, ct);
        await events.PublishAsync(job.Id);
        await events.PublishApprovalAsync(approval.Id, JobsHub.ApprovalRequested);

        return Results.Json(new { job = Describe(job), approval = ApprovalEndpoints.Describe(approval) }, JsonOptions, statusCode: 202);
    }

    private static async Task<IResult> List(
        HttpContext http, CerberusDbContext db, string? engagementId, string? state, int? limit, CancellationToken ct)
    {
        var userId = UserId(http);
        var query = db.Jobs.AsNoTracking().Where(j => j.UserId == userId);
        if (!string.IsNullOrWhiteSpace(engagementId)) query = query.Where(j => j.EngagementId == engagementId);
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!Enum.TryParse<JobState>(state, ignoreCase: true, out var parsed))
            {
                return Results.Json(new { error = $"state must be one of: {string.Join(", ", Enum.GetNames<JobState>())}" }, statusCode: 400);
            }
            var name = parsed.ToString();
            query = query.Where(j => j.State == name);
        }

        var jobs = await query.OrderByDescending(j => j.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync(ct);
        return Results.Json(jobs.Select(Describe), JsonOptions);
    }

    private static async Task<IResult> Get(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id && j.UserId == userId, ct);
        return job is null ? NotFound() : Results.Json(Describe(job), JsonOptions);
    }

    private static async Task<IResult> Cancel(
        string id, HttpContext http, CerberusDbContext db, JobControl control, CancellationToken ct)
    {
        var userId = UserId(http);
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id && j.UserId == userId, ct);
        if (job is null) return NotFound();

        var result = await control.CancelAsync(job, ct);
        var current = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == id, ct);
        return result switch
        {
            JobControl.CancelResult.Cancelled => Results.Json(Describe(current), JsonOptions),
            // The job stops cooperatively; poll GET /jobs/{id} for its final state.
            JobControl.CancelResult.StopRequested => Results.Json(Describe(current), JsonOptions, statusCode: 202),
            _ => Results.Json(new { error = $"Job has already finished ({current.State.ToLowerInvariant()})" }, statusCode: 409),
        };
    }

    /// <summary>
    /// A run's event stream from the database, in order. <c>after</c> is the last
    /// sequence the client already has; use it to catch up after a disconnect and
    /// then continue with live <c>jobEvent</c> messages from the hub.
    /// </summary>
    private static async Task<IResult> Events(
        string id, HttpContext http, CerberusDbContext db, long? after, int? limit, CancellationToken ct)
    {
        var userId = UserId(http);
        if (!await db.Jobs.AnyAsync(j => j.Id == id && j.UserId == userId, ct)) return NotFound();

        var take = Math.Clamp(limit ?? 500, 1, 2000);
        var events = await db.JobEvents.AsNoTracking()
            .Where(e => e.JobId == id && e.Sequence > (after ?? 0))
            .OrderBy(e => e.Sequence)
            .Take(take)
            .ToListAsync(ct);
        var lastSequence = await db.JobEvents.Where(e => e.JobId == id).MaxAsync(e => (long?)e.Sequence, ct) ?? 0;

        return Results.Json(new
        {
            jobId = id,
            events = events.Select(RunStream.Describe),
            lastSequence,
            // True when this page did not reach the end: ask again with after = the last sequence returned.
            more = events.Count > 0 && events[^1].Sequence < lastSequence,
        }, JsonOptions);
    }

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static IResult NotFound() => Results.Json(new { error = "Job not found" }, statusCode: 404);

    private static IResult BadRequest(string error) => Results.Json(new { error }, statusCode: 400);

    internal static object Describe(Job j) => new
    {
        j.Id, j.Type, j.State, j.Tier, j.EngagementId, j.TargetPath,
        j.ResultId, j.ApprovalId, j.Error, j.CreatedAt, j.StartedAt, j.FinishedAt,
    };
}
