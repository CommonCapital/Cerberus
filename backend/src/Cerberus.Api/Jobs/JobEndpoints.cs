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
    string? Model, int? MaxFindings, bool? IncludeTestsAndDocs);

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
                .Select(s => new { s.Id, s.TargetPath, s.EngagementId })
                .FirstOrDefaultAsync(ct);
            if (scan is null) return Results.Json(new { error = "Scan not found" }, statusCode: 404);
            if (engagementId is not null && engagementId != scan.EngagementId)
            {
                return BadRequest("That scan was not run under this engagement");
            }
            engagementId = scan.EngagementId;
            targetPath = scan.TargetPath;
            parameters = JsonSerializer.Serialize(
                new TriageJobParameters(scan.Id, body.Model, body.MaxFindings ?? 15, body.IncludeTestsAndDocs ?? false),
                JobTypes.Json);
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
            parameters = JsonSerializer.Serialize(new ScanJobParameters(body.Sca ?? true), JobTypes.Json);
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
        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(job.Id);
        await bus.PublishAsync(new JobMessage(job.Id), ct);

        return Results.Json(Describe(job), JsonOptions, statusCode: 202);
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

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static IResult NotFound() => Results.Json(new { error = "Job not found" }, statusCode: 404);

    private static IResult BadRequest(string error) => Results.Json(new { error }, statusCode: 400);

    internal static object Describe(Job j) => new
    {
        j.Id, j.Type, j.State, j.Tier, j.EngagementId, j.TargetPath,
        j.ResultId, j.Error, j.CreatedAt, j.StartedAt, j.FinishedAt,
    };
}
