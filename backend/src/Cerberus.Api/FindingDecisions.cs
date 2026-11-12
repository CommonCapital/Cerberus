using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerberus.Api.Audit;
using Cerberus.Api.Auth;
using Cerberus.Api.Jobs;
using Cerberus.Data;
using Cerberus.Scanning;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api;

public sealed record DecisionBody(string? FindingId, string? Status, string? Note);

public static class DecisionStatuses
{
    /// <summary>Real, and the operator has decided to live with it — for a stated reason.</summary>
    public const string AcceptedRisk = "accepted-risk";

    /// <summary>Not a problem here — for a stated reason.</summary>
    public const string FalsePositive = "false-positive";

    public static bool IsValid(string? status) => status is AcceptedRisk or FalsePositive;

    public static string Label(string status) => status == AcceptedRisk ? "accepted risk" : "false positive";
}

/// <summary>
/// The operator's decisions on findings (PLAN.md §9, a finding's status). A
/// decision is tied to what the finding is — its rule, file and code — not to
/// one scan, so it carries over to later scans of the same code, also when
/// lines above it move. Decisions are the operator's alone; a model cannot make one.
/// </summary>
public static partial class FindingDecisions
{
    public const int MaxNote = 2000;

    /// <summary>
    /// What makes a finding "the same one" across scans: the rule, the file and
    /// the code on the line (or, for a dependency, the package and version) —
    /// not the line number. Two identical lines in one file share a fingerprint.
    /// </summary>
    public static string Fingerprint(Finding finding)
    {
        var rule = finding.VulnId is { Length: > 0 } vuln
            ? vuln
            : finding.Id.IndexOf(':') is var colon and > 0 ? finding.Id[..colon] : finding.Category;
        var what = finding.Package is not null
            ? $"{finding.Package}@{finding.Version}"
            : Whitespace().Replace(SecretMasker.Mask(finding.Evidence), " ").Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{rule}\n{finding.File}\n{what}"));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static List<Finding> ReadFindings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Finding>>(json, TriageRunner.JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The user's decisions, by fingerprint.</summary>
    public static async Task<Dictionary<string, FindingDecision>> ForUserAsync(CerberusDbContext db, string userId, CancellationToken ct) =>
        await db.FindingDecisions.AsNoTracking().Where(d => d.UserId == userId).ToDictionaryAsync(d => d.Fingerprint, ct);

    public static object Describe(FindingDecision d) => new
    {
        id = d.Id,
        fingerprint = d.Fingerprint,
        status = d.Status,
        note = d.Note,
        ruleId = d.RuleId,
        file = d.File,
        title = d.Title,
        decidedAt = d.UpdatedAt,
    };

    // --- endpoints ------------------------------------------------------------

    public static void MapFindingDecisionEndpoints(this WebApplication app)
    {
        app.MapPut("/scans/{id}/findings/decision", Decide).AddEndpointFilter<RequireAuthFilter>();
        var decisions = app.MapGroup("/decisions").AddEndpointFilter<RequireAuthFilter>();
        decisions.MapGet("/", List);
        decisions.MapDelete("/{id}", Remove);
    }

    /// <summary>Records the operator's decision on one finding of a scan. It then applies to that finding in every scan.</summary>
    private static async Task<IResult> Decide(
        string id, DecisionBody body, HttpContext http, CerberusDbContext db, AuditLog audit, CancellationToken ct)
    {
        var userId = UserId(http);
        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (scan is null) return Results.Json(new { error = "Scan not found" }, statusCode: 404);

        if (!DecisionStatuses.IsValid(body.Status)) return BadRequest("status must be accepted-risk or false-positive");
        var note = body.Note?.Trim() ?? "";
        if (note.Length == 0) return BadRequest("note is required: say why");
        if (note.Length > MaxNote) return BadRequest($"note must be at most {MaxNote} characters");

        var finding = ReadFindings(scan.Findings).FirstOrDefault(f => f.Id == body.FindingId);
        if (finding is null) return Results.Json(new { error = "That finding is not part of this scan" }, statusCode: 404);

        var fingerprint = Fingerprint(finding);
        var decision = await db.FindingDecisions.FirstOrDefaultAsync(d => d.UserId == userId && d.Fingerprint == fingerprint, ct);
        if (decision is null)
        {
            decision = new FindingDecision { UserId = userId, Fingerprint = fingerprint };
            db.FindingDecisions.Add(decision);
        }
        decision.Status = body.Status!;
        decision.Note = note;
        decision.RuleId = finding.Id.IndexOf(':') is var colon and > 0 ? finding.Id[..colon] : finding.Category;
        decision.File = finding.File;
        decision.Title = finding.Title;
        decision.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.AppendAsync(userId, "finding.decide", AuditOutcome.Ok,
            $"Operator marked \"{finding.Title}\" in {finding.File} as {DecisionStatuses.Label(decision.Status)}: {note}",
            scan.EngagementId, scan.TargetPath, ct: ct);
        return Results.Json(Describe(decision));
    }

    private static async Task<IResult> List(HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var decisions = await db.FindingDecisions.AsNoTracking().Where(d => d.UserId == userId).OrderByDescending(d => d.UpdatedAt).ToListAsync(ct);
        return Results.Json(decisions.Select(Describe));
    }

    /// <summary>Withdraws a decision; the finding counts as open again everywhere.</summary>
    private static async Task<IResult> Remove(string id, HttpContext http, CerberusDbContext db, AuditLog audit, CancellationToken ct)
    {
        var userId = UserId(http);
        var decision = await db.FindingDecisions.FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId, ct);
        if (decision is null) return Results.Json(new { error = "Decision not found" }, statusCode: 404);

        db.FindingDecisions.Remove(decision);
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(userId, "finding.undecide", AuditOutcome.Ok,
            $"Operator withdrew the decision ({DecisionStatuses.Label(decision.Status)}) on \"{decision.Title}\" in {decision.File}.", ct: ct);
        return Results.NoContent();
    }

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static IResult BadRequest(string error) => Results.Json(new { error }, statusCode: 400);
}
