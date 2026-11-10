using System.Text.Json;
using System.Text.Json.Serialization;
using Cerberus.Api.Audit;
using Cerberus.Api.Auth;
using Cerberus.Authorization;
using Cerberus.Data;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api;

public sealed record CreateEngagementRequest(string? Name, string? MaxTier, DateTime? StartsAt, DateTime? EndsAt);

/// <summary>
/// Whether every run must name an engagement (PLAN.md §3: "every run belongs
/// to an Engagement"). Off by default for local use; turn on with
/// CERBERUS_REQUIRE_ENGAGEMENT=true.
/// </summary>
public sealed record RunPolicy(bool RequireEngagement);

public sealed record AddTargetRequest(string? Type, string? Locator, bool? OwnershipAttested);

/// <summary>
/// The authorization &amp; safety spine (PLAN.md §3, milestone M4): engagements,
/// their in-scope targets, the operator's abort switch, and the audit log.
/// </summary>
public static class EngagementEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private const int MaxTargetsPerEngagement = 200;

    public static void MapEngagementEndpoints(this WebApplication app)
    {
        var engagements = app.MapGroup("/engagements").AddEndpointFilter<RequireAuthFilter>();
        engagements.MapGet("/", List);
        engagements.MapPost("/", Create);
        engagements.MapGet("/{id}", Get);
        engagements.MapPost("/{id}/targets", AddTarget);
        engagements.MapDelete("/{id}/targets/{targetId}", RemoveTarget);
        engagements.MapGet("/{id}/targets/{targetId}/challenge", GetChallenge);
        engagements.MapPost("/{id}/targets/{targetId}/verify", VerifyTarget);
        engagements.MapPost("/{id}/abort", (string id, HttpContext http, CerberusDbContext db, AuditLog audit, Jobs.JobControl jobs, CancellationToken ct) =>
            End(id, EngagementStatus.Aborted, http, db, audit, jobs, ct));
        engagements.MapPost("/{id}/close", (string id, HttpContext http, CerberusDbContext db, AuditLog audit, Jobs.JobControl jobs, CancellationToken ct) =>
            End(id, EngagementStatus.Closed, http, db, audit, jobs, ct));

        var auditGroup = app.MapGroup("/audit").AddEndpointFilter<RequireAuthFilter>();
        auditGroup.MapGet("/", ListAudit);
        auditGroup.MapGet("/verify", VerifyAudit);
        auditGroup.MapGet("/export", ExportAudit);
    }

    // --- scope gate used by run endpoints -------------------------------------

    /// <summary>
    /// Dispatch-time gate for a run. Loads the engagement, applies
    /// <see cref="ScopePolicy"/>, and records the decision in the audit log.
    /// Returns null when the run may proceed, or the error response to send.
    /// </summary>
    public static async Task<IResult?> AuthorizeRunAsync(
        CerberusDbContext db, AuditLog audit, string userId, string engagementId,
        string action, CapabilityTier tier, string path, CancellationToken ct)
    {
        var decision = await CheckScopeAsync(db, audit, userId, engagementId, action, tier, path, ct);
        if (decision is null)
        {
            return Results.Json(new { error = "Engagement not found" }, statusCode: 404);
        }
        return decision.Allowed
            ? null
            : Results.Json(new { error = decision.Reason, engagementId }, statusCode: 403);
    }

    /// <summary>
    /// The scope decision for a run, recorded in the audit log. Null when the
    /// engagement does not exist (or belongs to someone else).
    /// </summary>
    public static async Task<ScopeDecision?> CheckScopeAsync(
        CerberusDbContext db, AuditLog audit, string userId, string engagementId,
        string action, CapabilityTier tier, string path, CancellationToken ct)
    {
        var engagement = await db.Engagements.AsNoTracking().Include(e => e.Targets)
            .FirstOrDefaultAsync(e => e.Id == engagementId && e.UserId == userId, ct);
        if (engagement is null) return null;

        var decision = ScopePolicy.Check(ToScope(engagement), tier, TargetType.Path, path, DateTime.UtcNow);
        await audit.AppendAsync(userId, action,
            decision.Allowed ? AuditOutcome.Allowed : AuditOutcome.Denied,
            decision.Reason, engagement.Id, path, tier, ct);
        return decision;
    }

    /// <summary>The refusal for a run submitted without an engagement when one is mandatory.</summary>
    public static IResult? RequireEngagement(RunPolicy policy, string? engagementId) =>
        engagementId is null && policy.RequireEngagement
            ? Results.Json(new { error = "An engagementId is required: this server only runs work under an engagement." }, statusCode: 400)
            : null;

    // --- engagements ----------------------------------------------------------

    private static async Task<IResult> List(HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var items = await db.Engagements.AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new
            {
                e.Id, e.Name, e.Status, e.MaxTier, e.StartsAt, e.EndsAt, e.CreatedAt, e.EndedAt,
                targetCount = e.Targets.Count,
            })
            .ToListAsync(ct);
        return Results.Json(items, JsonOptions);
    }

    private static async Task<IResult> Create(
        CreateEngagementRequest body, HttpContext http, CerberusDbContext db, AuditLog audit, CancellationToken ct)
    {
        var userId = UserId(http);
        var name = body.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 200)
        {
            return BadRequest("name is required (max 200 characters)");
        }

        var tier = CapabilityTier.T0;
        if (!string.IsNullOrWhiteSpace(body.MaxTier) &&
            !(Enum.TryParse(body.MaxTier, ignoreCase: true, out tier) && Enum.IsDefined(tier)))
        {
            return BadRequest("maxTier must be one of T0, T1, T2, T3");
        }
        if (tier > ScopePolicy.MaxGrantableTier)
        {
            return BadRequest(
                $"maxTier {tier} cannot be granted yet: it requires verified target ownership and an explicit authorization step, which are not implemented. The highest available tier is {ScopePolicy.MaxGrantableTier}.");
        }
        if (body.StartsAt is { } s && body.EndsAt is { } e && e <= s)
        {
            return BadRequest("endsAt must be after startsAt");
        }

        var engagement = new Engagement
        {
            UserId = userId,
            Name = name,
            MaxTier = tier.ToString(),
            StartsAt = body.StartsAt?.ToUniversalTime(),
            EndsAt = body.EndsAt?.ToUniversalTime(),
        };
        db.Engagements.Add(engagement);
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(userId, "engagement.create", AuditOutcome.Ok,
            $"Created engagement \"{name}\" (max tier {tier}).", engagement.Id, ct: ct);

        return Results.Json(Describe(engagement), JsonOptions, statusCode: 201);
    }

    private static async Task<IResult> Get(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var engagement = await Find(db, id, UserId(http), ct);
        return engagement is null ? NotFound() : Results.Json(Describe(engagement), JsonOptions);
    }

    /// <summary>Abort (kill switch) or close: either way no further runs are authorized.</summary>
    private static async Task<IResult> End(
        string id, EngagementStatus status, HttpContext http, CerberusDbContext db, AuditLog audit,
        Jobs.JobControl jobs, CancellationToken ct)
    {
        var userId = UserId(http);
        var engagement = await Find(db, id, userId, ct);
        if (engagement is null) return NotFound();
        if (engagement.Status != nameof(EngagementStatus.Active))
        {
            return Results.Json(new { error = $"Engagement is already {engagement.Status.ToLowerInvariant()}" }, statusCode: 409);
        }

        engagement.Status = status.ToString();
        engagement.EndedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(userId,
            status == EngagementStatus.Aborted ? "engagement.abort" : "engagement.close",
            AuditOutcome.Ok, $"Engagement {status.ToString().ToLowerInvariant()}; no further runs will be authorized.",
            engagement.Id, ct: ct);

        // Kill switch: stop the engagement's queued and running jobs. A normal
        // close leaves work in flight to finish.
        var stopped = status == EngagementStatus.Aborted
            ? await jobs.CancelEngagementAsync(userId, engagement.Id, ct)
            : new Jobs.CancelSummary(0, 0);

        return Results.Json(new
        {
            engagement = Describe(engagement),
            jobsCancelled = new { queued = stopped.Queued, running = stopped.Running },
        }, JsonOptions);
    }

    // --- targets --------------------------------------------------------------

    private static async Task<IResult> AddTarget(
        string id, AddTargetRequest body, HttpContext http, CerberusDbContext db,
        AuditLog audit, Cerberus.Scanning.ScanOptions scanOptions, CancellationToken ct)
    {
        var userId = UserId(http);
        var engagement = await Find(db, id, userId, ct);
        if (engagement is null) return NotFound();
        if (engagement.Status != nameof(EngagementStatus.Active))
        {
            return Results.Json(new { error = $"Engagement is {engagement.Status.ToLowerInvariant()}; its scope can no longer change" }, statusCode: 409);
        }

        if (!Enum.TryParse<TargetType>(body.Type ?? nameof(TargetType.Path), ignoreCase: true, out var type) || !Enum.IsDefined(type))
        {
            return BadRequest("type must be one of path, host, domain");
        }
        var locator = ScopePolicy.Normalize(type, body.Locator);
        if (locator is null)
        {
            return BadRequest(type == TargetType.Path
                ? "locator must be a path relative to the scan root, without '..'"
                : "locator must be a valid host or domain name");
        }

        VerificationStatus verification;
        if (type == TargetType.Path)
        {
            // Code targets: the operator must state they own or were supplied the code.
            if (body.OwnershipAttested != true)
            {
                return BadRequest("ownershipAttested must be true: confirm you own this code or are authorized to test it");
            }
            var full = Path.GetFullPath(Path.Combine(Path.GetFullPath(scanOptions.ScanRoot), locator));
            if (!Directory.Exists(full) && !File.Exists(full))
            {
                return BadRequest("locator does not exist under the scan root");
            }
            verification = VerificationStatus.Attested;
        }
        else
        {
            // Live targets start unverified; see GET .../challenge and POST .../verify.
            verification = VerificationStatus.Pending;
        }

        if (engagement.Targets.Count >= MaxTargetsPerEngagement)
        {
            return BadRequest($"An engagement can hold at most {MaxTargetsPerEngagement} targets");
        }
        if (engagement.Targets.Any(t => t.Type == type.ToString() && t.Locator == locator))
        {
            return Results.Json(new { error = "That target is already in scope" }, statusCode: 409);
        }

        var target = new EngagementTarget
        {
            EngagementId = engagement.Id,
            Type = type.ToString(),
            Locator = locator,
            Verification = verification.ToString(),
            ChallengeToken = type == TargetType.Path ? null : OwnershipVerifier.NewToken(),
        };
        db.EngagementTargets.Add(target);
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(userId, "target.add", AuditOutcome.Ok,
            $"Added {type.ToString().ToLowerInvariant()} target to scope ({verification.ToString().ToLowerInvariant()}).",
            engagement.Id, DisplayLocator(target), ct: ct);

        return Results.Json(DescribeTarget(target), JsonOptions, statusCode: 201);
    }

    private static async Task<IResult> RemoveTarget(
        string id, string targetId, HttpContext http, CerberusDbContext db, AuditLog audit, CancellationToken ct)
    {
        var userId = UserId(http);
        var engagement = await Find(db, id, userId, ct);
        if (engagement is null) return NotFound();

        var target = engagement.Targets.FirstOrDefault(t => t.Id == targetId);
        if (target is null) return Results.Json(new { error = "Target not found" }, statusCode: 404);

        db.EngagementTargets.Remove(target);
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(userId, "target.remove", AuditOutcome.Ok,
            $"Removed {target.Type.ToLowerInvariant()} target from scope.", engagement.Id, DisplayLocator(target), ct: ct);

        return Results.Json(new { removed = target.Id });
    }

    // --- proof of control -----------------------------------------------------

    private static async Task<IResult> GetChallenge(
        string id, string targetId, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var engagement = await Find(db, id, UserId(http), ct);
        if (engagement is null) return NotFound();
        var target = engagement.Targets.FirstOrDefault(t => t.Id == targetId);
        if (target is null) return Results.Json(new { error = "Target not found" }, statusCode: 404);
        if (!TryGetChallenge(target, out var type, out var challenge))
        {
            return BadRequest("Path targets are attested when added; only host and domain targets have a challenge");
        }

        return Results.Json(new
        {
            targetId = target.Id,
            type = target.Type,
            locator = target.Locator,
            verification = target.Verification,
            value = challenge.ExpectedValue,
            dns = challenge.DnsRecordName is null ? null : new
            {
                recordType = "TXT",
                name = challenge.DnsRecordName,
                value = challenge.ExpectedValue,
            },
            http = new
            {
                urls = challenge.HttpUrls.Select(u => u.ToString()),
                body = challenge.ExpectedValue,
            },
            instructions = type == TargetType.Domain
                ? "Publish the value as a DNS TXT record at the name shown, or serve it as the body of the well-known file, then call verify."
                : "Serve the value as the body of the well-known file (or publish the TXT record, if the host has a DNS name), then call verify.",
        }, JsonOptions);
    }

    private static async Task<IResult> VerifyTarget(
        string id, string targetId, HttpContext http, CerberusDbContext db, AuditLog audit,
        IOwnershipProbe probe, string? method, CancellationToken ct)
    {
        var userId = UserId(http);
        var engagement = await Find(db, id, userId, ct);
        if (engagement is null) return NotFound();
        var target = engagement.Targets.FirstOrDefault(t => t.Id == targetId);
        if (target is null) return Results.Json(new { error = "Target not found" }, statusCode: 404);
        if (!TryGetChallenge(target, out var type, out _))
        {
            return BadRequest("Path targets are attested when added; only host and domain targets are verified");
        }
        if (engagement.Status != nameof(EngagementStatus.Active))
        {
            return Results.Json(new { error = $"Engagement is {engagement.Status.ToLowerInvariant()}" }, statusCode: 409);
        }

        ProofMethod? only = null;
        if (!string.IsNullOrWhiteSpace(method))
        {
            if (!Enum.TryParse<ProofMethod>(method, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return BadRequest("method must be dns or http");
            }
            only = parsed;
        }

        var outcome = await OwnershipVerifier.VerifyAsync(type, target.Locator, target.ChallengeToken!, only, probe, ct);
        if (outcome.Verified)
        {
            target.Verification = nameof(VerificationStatus.Verified);
            target.VerificationMethod = outcome.Method.ToString();
            target.VerifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        await audit.AppendAsync(userId, "target.verify",
            outcome.Verified ? AuditOutcome.Ok : AuditOutcome.Denied, outcome.Detail,
            engagement.Id, target.Locator, ct: ct);

        return Results.Json(new
        {
            verified = outcome.Verified,
            method = outcome.Method,
            detail = outcome.Detail,
            target = DescribeTarget(target),
        }, JsonOptions, statusCode: outcome.Verified ? 200 : 422);
    }

    private static bool TryGetChallenge(EngagementTarget target, out TargetType type, out OwnershipChallenge challenge)
    {
        challenge = null!;
        if (!Enum.TryParse(target.Type, out type) || type == TargetType.Path || target.ChallengeToken is null)
        {
            return false;
        }
        challenge = OwnershipVerifier.Describe(type, target.Locator, target.ChallengeToken);
        return true;
    }

    // --- audit log ------------------------------------------------------------

    /// <summary>
    /// Download the caller's whole audit log, oldest first, with the chain
    /// hashes — as JSON Lines (default) or CSV. The export can be re-verified
    /// offline: each line's hash covers its fields and the previous hash.
    /// </summary>
    private static async Task<IResult> ExportAudit(
        HttpContext http, CerberusDbContext db, string? format, CancellationToken ct)
    {
        var userId = UserId(http);
        var csv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
        if (!csv && !string.IsNullOrWhiteSpace(format) && !string.Equals(format, "jsonl", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("format must be jsonl or csv");
        }

        var events = await db.AuditEvents.AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.Sequence)
            .ToListAsync(ct);

        var text = new System.Text.StringBuilder();
        if (csv)
        {
            text.Append("sequence,createdAt,action,outcome,engagementId,target,tier,detail,previousHash,hash\r\n");
            foreach (var e in events)
            {
                text.AppendJoin(',', new[]
                {
                    e.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    e.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    Csv(e.Action), Csv(e.Outcome), Csv(e.EngagementId), Csv(e.Target), Csv(e.Tier), Csv(e.Detail),
                    e.PreviousHash, e.Hash,
                }).Append("\r\n");
            }
        }
        else
        {
            foreach (var e in events)
            {
                text.Append(JsonSerializer.Serialize(new
                {
                    e.Sequence, e.CreatedAt, e.Action, e.Outcome, e.EngagementId, e.Target, e.Tier, e.Detail,
                    e.PreviousHash, e.Hash,
                }, JsonOptions)).Append('\n');
            }
        }

        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(text.ToString()),
            csv ? "text/csv; charset=utf-8" : "application/x-ndjson",
            $"cerberus-audit-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{(csv ? "csv" : "jsonl")}");
    }

    /// <summary>Quotes a CSV field and neutralizes values a spreadsheet would run as a formula.</summary>
    private static string Csv(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }


    private static async Task<IResult> ListAudit(
        HttpContext http, CerberusDbContext db, string? engagementId, int? limit, long? before, CancellationToken ct)
    {
        var userId = UserId(http);
        var take = Math.Clamp(limit ?? 100, 1, 1000);

        var query = db.AuditEvents.AsNoTracking().Where(a => a.UserId == userId);
        if (!string.IsNullOrWhiteSpace(engagementId)) query = query.Where(a => a.EngagementId == engagementId);
        if (before is { } b) query = query.Where(a => a.Sequence < b);

        var events = await query.OrderByDescending(a => a.Sequence).Take(take)
            .Select(a => new
            {
                a.Sequence, a.CreatedAt, a.Action, a.Outcome, a.EngagementId, a.Target, a.Tier, a.Detail, a.Hash,
            })
            .ToListAsync(ct);
        return Results.Json(events, JsonOptions);
    }

    /// <summary>Re-computes the caller's hash chain and reports whether it is intact.</summary>
    private static async Task<IResult> VerifyAudit(HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var events = await db.AuditEvents.AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.Sequence)
            .ToListAsync(ct);

        var result = AuditChain.Verify(events.Select(AuditLog.ToChained));
        return Results.Json(new
        {
            intact = result.Intact,
            eventsChecked = result.Checked,
            totalEvents = events.Count,
            firstBrokenSequence = result.FirstBrokenSequence,
            problem = result.Problem,
            headHash = events.Count > 0 ? events[^1].Hash : null,
        }, JsonOptions);
    }

    // --- helpers --------------------------------------------------------------

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static Task<Engagement?> Find(CerberusDbContext db, string id, string userId, CancellationToken ct) =>
        db.Engagements.Include(e => e.Targets).FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId, ct);

    private static IResult NotFound() => Results.Json(new { error = "Engagement not found" }, statusCode: 404);

    private static IResult BadRequest(string error) => Results.Json(new { error }, statusCode: 400);

    private static EngagementScope ToScope(Engagement e) => new(
        Enum.TryParse<EngagementStatus>(e.Status, out var status) ? status : EngagementStatus.Closed,
        Enum.TryParse<CapabilityTier>(e.MaxTier, out var tier) ? tier : CapabilityTier.T0,
        e.StartsAt, e.EndsAt,
        e.Targets
            .Where(t => Enum.TryParse<TargetType>(t.Type, out _))
            .Select(t => new ScopedTarget(
                Enum.Parse<TargetType>(t.Type), t.Locator,
                Enum.TryParse<VerificationStatus>(t.Verification, out var v) ? v : VerificationStatus.Pending))
            .ToList());

    private static string DisplayLocator(EngagementTarget t) =>
        t.Type == nameof(TargetType.Path) && t.Locator.Length == 0 ? "." : t.Locator;

    private static object DescribeTarget(EngagementTarget t) => new
    {
        t.Id, t.Type, locator = DisplayLocator(t), t.Verification, t.VerificationMethod, t.VerifiedAt, t.CreatedAt,
    };

    private static object Describe(Engagement e) => new
    {
        e.Id, e.Name, e.Status, e.MaxTier, e.StartsAt, e.EndsAt, e.CreatedAt, e.EndedAt,
        targets = e.Targets.OrderBy(t => t.CreatedAt).Select(DescribeTarget),
    };
}
