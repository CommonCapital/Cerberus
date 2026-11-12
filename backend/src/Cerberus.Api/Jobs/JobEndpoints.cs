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
    string? Model, int? MaxFindings, bool? IncludeTestsAndDocs, string? Exclusions, List<string>? Catalogs,
    int? MaxSteps = null, string? Instructions = null, string? AgentModel = null,
    string? AssessmentId = null, int? MaxControls = null, List<string>? ControlIds = null, List<string>? Agents = null);

public sealed record JobMessageRequest(string? Text);

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
        jobs.MapPost("/{id}/messages", Message);
    }

    /// <summary>Says something to the agent of a running job; it reads it before its next step.</summary>
    private static async Task<IResult> Message(
        string id, JobMessageRequest body, HttpContext http, CerberusDbContext db, Agents.OperatorInbox inbox,
        RunStream stream, AuditLog audit, CancellationToken ct)
    {
        var userId = UserId(http);
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id && j.UserId == userId, ct);
        if (job is null) return NotFound();

        var text = body.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return BadRequest("text is required");
        if (text.Length > Agents.OperatorInbox.MaxMessageLength)
        {
            return BadRequest($"text must be at most {Agents.OperatorInbox.MaxMessageLength} characters");
        }

        if (!inbox.TryPost(job.Id, text))
        {
            return Results.Json(new { error = "This run is not taking messages: only a running agent does." }, statusCode: 409);
        }
        await stream.EmitAsync(job.Id, userId, RunEventTypes.OperatorMessage, new { text });
        await audit.AppendAsync(userId, "job.message", AuditOutcome.Ok,
            $"Operator message to the agent of job {job.Id} ({text.Length} characters).", job.EngagementId, job.TargetPath, ct: ct);
        return Results.Json(new { jobId = job.Id, queued = true }, statusCode: 202);
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
        ApprovalNeed? planNeed = null;
        var sendsCodeOut = false;

        if (type == JobTypes.Review)
        {
            // Which catalogs: the ones named, or every installed one.
            var wanted = body.Catalogs is { Count: > 0 }
                ? body.Catalogs.Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : catalogs.All.Select(c => c.Id).Order(StringComparer.Ordinal).ToList();
            if (wanted.FirstOrDefault(id => catalogs.Get(id) is null) is { } unknown)
            {
                return BadRequest($"Unknown catalog '{unknown}'");
            }
            wanted = wanted.Select(id => catalogs.Get(id)!.Id).ToList();

            if (body.Model is not null && !Cerberus.Core.Models.ModelRegistry.IsSupported(body.Model))
            {
                return BadRequest("model must be a supported model id, or be left out for a review without model triage");
            }
            if (body.MaxFindings is < 1 or > TriageRunner.MaxFindingsLimit)
            {
                return BadRequest($"maxFindings must be between 1 and {TriageRunner.MaxFindingsLimit}");
            }
            if (body.AgentModel is not null && !Cerberus.Core.Models.ModelRegistry.IsSupported(body.AgentModel))
            {
                return BadRequest("agentModel must be a supported model id, or be left out for a review without the SAST agent");
            }
            if (body.MaxSteps is < 1 or > Agents.SastAgent.MaxSteps)
            {
                return BadRequest($"maxSteps must be between 1 and {Agents.SastAgent.MaxSteps}");
            }
            List<string>? reviewAgents = null;
            if (body.Agents is { Count: > 0 })
            {
                if (body.AgentModel is null) return BadRequest("agents needs agentModel: the model that runs them");
                reviewAgents = body.Agents.Select(a => a.Trim().ToLowerInvariant()).Distinct().ToList();
                if (reviewAgents.FirstOrDefault(a => !ReviewAgents.All.Contains(a)) is { } unknownAgent)
                {
                    return BadRequest($"Unknown agent '{unknownAgent}'. A review can dispatch: {string.Join(", ", ReviewAgents.All)}");
                }
                // In the order they run, whatever order they were named in.
                reviewAgents = ReviewAgents.All.Where(reviewAgents.Contains).ToList();
            }
            var reviewExclusions = string.IsNullOrWhiteSpace(body.Exclusions) ? ScanExclusions.Ask : body.Exclusions.Trim().ToLowerInvariant();
            if (!ScanExclusions.IsValid(reviewExclusions, allowAsk: true))
            {
                return BadRequest("exclusions must be ask, honour or ignore");
            }

            var review = new ReviewJobParameters(wanted, body.Sca ?? true, reviewExclusions, body.Model, body.MaxFindings ?? 15,
                body.AgentModel, body.MaxSteps ?? Agents.SastAgent.DefaultSteps, reviewAgents);
            parameters = JsonSerializer.Serialize(review, JobTypes.Json);

            // Plan first: the operator approves exactly the steps that will run.
            var plan = ReviewPlan.Build(targetPath, review, catalogs);
            planNeed = new ApprovalNeed(ActionKinds.RunPlan, $"Run this review of \"{targetPath}\" ({plan.Count} steps)", ReviewPlan.Describe(plan));
            sendsCodeOut = new[] { body.Model, body.AgentModel }.Any(m => m is not null &&
                !Cerberus.Triage.FindingTriager.IsLocal(Cerberus.Core.Models.ModelRegistry.Find(m)!.Provider));
        }
        else if (type == JobTypes.Sast)
        {
            // No default model: the caller decides where the code under review is sent.
            if (string.IsNullOrWhiteSpace(body.Model) || !Cerberus.Core.Models.ModelRegistry.IsSupported(body.Model))
            {
                return BadRequest("model is required and must be a supported model id (a local Ollama model keeps the code on this machine)");
            }
            if (body.MaxSteps is < 1 or > Agents.SastAgent.MaxSteps)
            {
                return BadRequest($"maxSteps must be between 1 and {Agents.SastAgent.MaxSteps}");
            }
            if (body.Instructions is { Length: > Agents.SastAgent.MaxInstructions })
            {
                return BadRequest($"instructions must be at most {Agents.SastAgent.MaxInstructions} characters");
            }
            var sast = new Agents.SastJobParameters(body.Model, body.MaxSteps ?? Agents.SastAgent.DefaultSteps,
                string.IsNullOrWhiteSpace(body.Instructions) ? null : body.Instructions.Trim());
            parameters = JsonSerializer.Serialize(sast, JobTypes.Json);

            var provider = Cerberus.Core.Models.ModelRegistry.Find(body.Model)!.Provider;
            if (!Cerberus.Triage.FindingTriager.IsLocal(provider))
            {
                approvalNeed = new ApprovalNeed(
                    ApprovalKinds.SendCodeToCloudModel,
                    $"Let {body.Model} ({provider}) read the code under \"{targetPath}\"",
                    $"The SAST agent decides for itself which files under \"{targetPath}\" to read, in up to {sast.MaxSteps} steps; " +
                    $"whatever it reads is sent to {provider}. Credential-shaped values are masked before sending; other code is sent as is. " +
                    "It can only read — it cannot change files or run anything. Nothing is sent until you approve.");
            }
        }
        else if (type == JobTypes.Index)
        {
            // Nothing to choose: the path is the request. The embedding model is the server's, and local.
            parameters = "{}";
        }
        else if (type == JobTypes.ThreatModel)
        {
            if (string.IsNullOrWhiteSpace(body.Model) || !Cerberus.Core.Models.ModelRegistry.IsSupported(body.Model))
            {
                return BadRequest("model is required and must be a supported model id (a local Ollama model keeps the code on this machine)");
            }
            if (body.MaxSteps is < 1 or > Agents.ThreatModelAgent.MaxSteps)
            {
                return BadRequest($"maxSteps must be between 1 and {Agents.ThreatModelAgent.MaxSteps}");
            }
            if (body.Instructions is { Length: > Agents.ThreatModelAgent.MaxInstructions })
            {
                return BadRequest($"instructions must be at most {Agents.ThreatModelAgent.MaxInstructions} characters");
            }
            var threatModel = new Agents.ThreatModelJobParameters(body.Model, body.MaxSteps ?? Agents.ThreatModelAgent.DefaultSteps,
                string.IsNullOrWhiteSpace(body.Instructions) ? null : body.Instructions.Trim());
            parameters = JsonSerializer.Serialize(threatModel, JobTypes.Json);

            var provider = Cerberus.Core.Models.ModelRegistry.Find(body.Model)!.Provider;
            if (!Cerberus.Triage.FindingTriager.IsLocal(provider))
            {
                approvalNeed = new ApprovalNeed(
                    ApprovalKinds.SendCodeToCloudModel,
                    $"Let {body.Model} ({provider}) read the code under \"{targetPath}\" for a threat model",
                    $"The threat-model agent decides for itself which files under \"{targetPath}\" to read, in up to {threatModel.MaxSteps} steps; " +
                    $"whatever it reads is sent to {provider}. Credential-shaped values are masked before sending; other code is sent as is. " +
                    "It can only read — it cannot change files or run anything. Nothing is sent until you approve.");
            }
        }
        else if (type == JobTypes.Evidence)
        {
            if (string.IsNullOrWhiteSpace(body.AssessmentId)) return BadRequest("assessmentId is required for an evidence job");
            if (string.IsNullOrWhiteSpace(body.Model) || !Cerberus.Core.Models.ModelRegistry.IsSupported(body.Model))
            {
                return BadRequest("model is required and must be a supported model id (a local Ollama model keeps the code on this machine)");
            }
            if (body.MaxControls is < 1 or > Agents.EvidenceAgent.MaxControls)
            {
                return BadRequest($"maxControls must be between 1 and {Agents.EvidenceAgent.MaxControls}");
            }
            if (body.MaxSteps is < 1 or > Agents.EvidenceAgent.MaxSteps)
            {
                return BadRequest($"maxSteps must be between 1 and {Agents.EvidenceAgent.MaxSteps}");
            }

            var assessment = await db.ComplianceAssessments.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == body.AssessmentId && a.UserId == userId, ct);
            if (assessment is null) return Results.Json(new { error = "Assessment not found" }, statusCode: 404);
            if (engagementId is not null && engagementId != assessment.EngagementId)
            {
                return BadRequest("That assessment was not run under this engagement");
            }
            engagementId = assessment.EngagementId;
            targetPath = assessment.TargetPath;

            var evidence = new Agents.EvidenceJobParameters(
                assessment.Id, body.Model, body.MaxControls ?? Agents.EvidenceAgent.DefaultControls,
                body.MaxSteps ?? Agents.EvidenceAgent.DefaultSteps,
                body.ControlIds is { Count: > 0 } ? body.ControlIds.Select(c => c.Trim()).ToList() : null);
            var matrix = Agents.EvidenceAgent.ReadMatrix(assessment);
            if (evidence.ControlIds?.FirstOrDefault(id => !matrix.Any(c => string.Equals(c.ControlId, id, StringComparison.OrdinalIgnoreCase))) is { } unknownControl)
            {
                return BadRequest($"Control '{unknownControl}' is not part of that assessment");
            }
            parameters = JsonSerializer.Serialize(evidence, JobTypes.Json);

            var provider = Cerberus.Core.Models.ModelRegistry.Find(body.Model)!.Provider;
            if (!Cerberus.Triage.FindingTriager.IsLocal(provider))
            {
                var count = Agents.EvidenceAgent.Select(matrix, Agents.EvidenceAgent.AnsweredControls(assessment), evidence).Count;
                approvalNeed = new ApprovalNeed(
                    ApprovalKinds.SendCodeToCloudModel,
                    $"Let {body.Model} ({provider}) read the code under \"{targetPath}\" for compliance evidence",
                    $"The evidence agent looks for evidence for {count} control(s) of assessment {assessment.Id}. It decides for itself which files under " +
                    $"\"{targetPath}\" to read, in up to {evidence.MaxSteps} steps; whatever it reads is sent to {provider}. " +
                    "Credential-shaped values are masked before sending; other code is sent as is. It can only read. Nothing is sent until you approve.");
            }
        }
        else if (type == JobTypes.Triage)
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
        // Permission mode: which of the things this job does must the operator still approve?
        var (permissions, permissionScope) = await PermissionEndpoints.ResolveAsync(db, userId, engagementId, ct);
        var involved = new List<string> { ActionKinds.RunJob };
        if (planNeed is not null) involved.Add(ActionKinds.RunPlan);
        if (approvalNeed is not null || sendsCodeOut) involved.Add(ActionKinds.SendCodeToCloudModel);
        var unapproved = permissions.Unapproved(involved);

        if (unapproved.Count == 0)
        {
            // Would have asked about some of this, but the operator said "always allow". Say so in the log.
            var skipped = involved.Where(k => k != ActionKinds.RunJob).ToList();
            if (skipped.Count > 0)
            {
                await audit.AppendAsync(userId, "approval.auto", AuditOutcome.Allowed,
                    $"Not asked: \"{(planNeed ?? approvalNeed)!.Title}\" is always allowed by the {permissionScope} permissions ({string.Join(", ", skipped)}).",
                    engagementId, targetPath, tier, ct);
            }
            approvalNeed = null;
        }
        else if (planNeed is not null)
        {
            // A review is approved as a whole, plan in hand — whichever of its parts needed approval.
            approvalNeed = planNeed with
            {
                Kind = unapproved.Contains(ActionKinds.SendCodeToCloudModel) ? ActionKinds.SendCodeToCloudModel : unapproved[^1],
            };
        }
        else if (!unapproved.Contains(ActionKinds.SendCodeToCloudModel))
        {
            approvalNeed = new ApprovalNeed(
                ActionKinds.RunJob,
                $"Run a {type} of \"{targetPath}\"",
                "The permission mode is \"ask\": every run waits for your approval, including passive ones. " +
                "Approve to start this job; choose \"always allow\" to stop being asked about starting jobs.");
        }

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
            Kinds = JsonSerializer.Serialize(unapproved),
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
