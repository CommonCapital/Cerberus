using System.Text.Json;
using System.Text.Json.Serialization;
using Cerberus.Api.Audit;
using Cerberus.Api.Auth;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Jobs;

/// <summary>Why a job must wait for the operator, and what they are being asked to allow.</summary>
public sealed record ApprovalNeed(string Kind, string Title, string Detail);

public sealed record DecisionRequest(string? Note);

public static class ApprovalKinds
{
    /// <summary>Source code (with secrets masked) will be sent to a model provider outside this machine.</summary>
    public const string SendCodeToCloudModel = "send-code-to-cloud-model";
}

public static class ApprovalStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Denied = "Denied";

    /// <summary>The job was cancelled (or its engagement aborted) before anyone decided.</summary>
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// Human in the loop (PLAN.md §6.5): a job that needs consent is stored in
/// <c>AwaitingApproval</c> with a request describing exactly what will happen.
/// Nothing runs until the operator approves; a denial ends the job cleanly.
/// The wait lives in the database, so it survives restarts and can be answered
/// from any client. Each request covers one job and nothing else.
/// </summary>
public static class ApprovalEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private const int MaxNoteLength = 1000;

    public static void MapApprovalEndpoints(this WebApplication app)
    {
        var approvals = app.MapGroup("/approvals").AddEndpointFilter<RequireAuthFilter>();
        approvals.MapGet("/", List);
        approvals.MapGet("/{id}", Get);
        approvals.MapPost("/{id}/approve", (string id, DecisionRequest? body, HttpContext http, CerberusDbContext db,
            AuditLog audit, IMessageBus bus, JobEvents events, CancellationToken ct) =>
            Decide(id, approve: true, body, http, db, audit, bus, events, ct));
        approvals.MapPost("/{id}/deny", (string id, DecisionRequest? body, HttpContext http, CerberusDbContext db,
            AuditLog audit, IMessageBus bus, JobEvents events, CancellationToken ct) =>
            Decide(id, approve: false, body, http, db, audit, bus, events, ct));
    }

    private static async Task<IResult> List(
        HttpContext http, CerberusDbContext db, string? status, string? engagementId, int? limit, CancellationToken ct)
    {
        var userId = UserId(http);
        var query = db.ApprovalRequests.AsNoTracking().Where(a => a.UserId == userId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var wanted = new[] { ApprovalStatus.Pending, ApprovalStatus.Approved, ApprovalStatus.Denied, ApprovalStatus.Cancelled }
                .FirstOrDefault(s => s.Equals(status, StringComparison.OrdinalIgnoreCase));
            if (wanted is null) return Results.Json(new { error = "status must be pending, approved, denied or cancelled" }, statusCode: 400);
            query = query.Where(a => a.Status == wanted);
        }
        if (!string.IsNullOrWhiteSpace(engagementId)) query = query.Where(a => a.EngagementId == engagementId);

        var items = await query.OrderByDescending(a => a.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync(ct);
        return Results.Json(items.Select(Describe), JsonOptions);
    }

    private static async Task<IResult> Get(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var approval = await db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
        return approval is null ? NotFound() : Results.Json(Describe(approval), JsonOptions);
    }

    private static async Task<IResult> Decide(
        string id, bool approve, DecisionRequest? body, HttpContext http, CerberusDbContext db,
        AuditLog audit, IMessageBus bus, JobEvents events, CancellationToken ct)
    {
        var userId = UserId(http);
        var approval = await db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
        if (approval is null) return NotFound();

        var note = body?.Note?.Trim();
        if (note?.Length > MaxNoteLength) return Results.Json(new { error = $"note is limited to {MaxNoteLength} characters" }, statusCode: 400);
        if (string.IsNullOrEmpty(note)) note = null;

        // Decide exactly once: only a still-pending request can change.
        var now = DateTime.UtcNow;
        var decision = approve ? ApprovalStatus.Approved : ApprovalStatus.Denied;
        var decided = await db.ApprovalRequests
            .Where(a => a.Id == id && a.Status == ApprovalStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, decision)
                .SetProperty(a => a.Note, note)
                .SetProperty(a => a.DecidedAt, now), ct);
        if (decided != 1)
        {
            return Results.Json(new { error = $"This request is already {approval.Status.ToLowerInvariant()}" }, statusCode: 409);
        }

        await audit.AppendAsync(userId, approve ? "approval.approve" : "approval.deny", AuditOutcome.Ok,
            $"{(approve ? "Approved" : "Denied")}: {approval.Title}" + (note is null ? "." : $" — {note}"),
            approval.EngagementId, ct: ct);

        if (approve)
        {
            // Release the job to the workers. It is authorized again when it starts.
            var released = await db.Jobs
                .Where(j => j.Id == approval.JobId && j.State == nameof(JobState.AwaitingApproval))
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, nameof(JobState.Queued)), ct);
            if (released == 1) await bus.PublishAsync(new JobMessage(approval.JobId), ct);
        }
        else
        {
            var reason = "Denied by the operator" + (note is null ? "." : $": {note}");
            await db.Jobs
                .Where(j => j.Id == approval.JobId && j.State == nameof(JobState.AwaitingApproval))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.State, nameof(JobState.Cancelled))
                    .SetProperty(j => j.Error, reason)
                    .SetProperty(j => j.FinishedAt, now), ct);
        }

        await events.PublishApprovalAsync(id, JobsHub.ApprovalResolved);
        await events.PublishAsync(approval.JobId);

        var current = await db.ApprovalRequests.AsNoTracking().FirstAsync(a => a.Id == id, ct);
        return Results.Json(Describe(current), JsonOptions);
    }

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static IResult NotFound() => Results.Json(new { error = "Approval request not found" }, statusCode: 404);

    internal static object Describe(ApprovalRequest a) => new
    {
        a.Id, a.JobId, a.EngagementId, a.Kind, a.Title, a.Detail, a.Status, a.Note, a.CreatedAt, a.DecidedAt,
    };
}
