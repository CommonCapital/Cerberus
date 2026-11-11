using Cerberus.Api.Auth;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Jobs;

/// <summary>
/// Live job updates over SignalR (<c>/hub/jobs</c>). A client connects with its
/// bearer token — as the <c>access_token</c> query parameter (what SignalR
/// clients send for WebSockets) or an Authorization header — and then receives
/// a <c>jobUpdated</c> message, carrying the same shape as <c>GET /jobs/{id}</c>,
/// whenever one of its own jobs changes state, plus <c>approvalRequested</c> /
/// <c>approvalResolved</c> when a job waits for, or receives, the operator's
/// decision. Every one of those, and everything a run reports while it works
/// (steps, progress, findings, model text), also arrives as an ordered
/// <c>jobEvent</c> that can be replayed from <c>GET /jobs/{id}/events</c>.
/// The hub is receive-only; decisions go through <c>/approvals</c>.
/// </summary>
public sealed class JobsHub : Hub
{
    public const string JobUpdated = "jobUpdated";

    /// <summary>One event of a run's stream: <c>{ jobId, sequence, type, data, createdAt }</c>.</summary>
    public const string JobEvent = "jobEvent";

    /// <summary>A job is waiting for the operator's decision; carries the approval request.</summary>
    public const string ApprovalRequested = "approvalRequested";

    /// <summary>An approval request was approved, denied or cancelled.</summary>
    public const string ApprovalResolved = "approvalResolved";

    public static string GroupFor(string userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var token = http?.Request.Query["access_token"].FirstOrDefault()
            ?? RequireAuthFilter.ExtractBearerToken(http?.Request.Headers.Authorization);

        string? userId = null;
        if (!string.IsNullOrWhiteSpace(token) && http is not null)
        {
            var auth = http.RequestServices.GetRequiredService<AuthService>();
            userId = await auth.ValidateTokenAsync(token, Context.ConnectionAborted);
        }
        if (userId is null)
        {
            Context.Abort();   // no valid session: never joins a group, receives nothing
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(userId), Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}

/// <summary>
/// Publishes a job's current state to its owner's connections. Best-effort:
/// a failed notification is logged and never affects the job itself — the
/// database stays the source of truth and <c>GET /jobs/{id}</c> always works.
/// </summary>
public sealed class JobEvents(
    IHubContext<JobsHub> hub, IServiceScopeFactory scopes, RunStream stream, ILogger<JobEvents> logger)
{
    public async Task PublishAsync(string jobId)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId);
            if (job is null) return;

            var described = JobEndpoints.Describe(job);
            await hub.Clients.Group(JobsHub.GroupFor(job.UserId)).SendAsync(JobsHub.JobUpdated, described);
            await stream.EmitAsync(job.Id, job.UserId, RunEventTypes.State, described);
            if (Enum.TryParse<JobState>(job.State, out var state) && JobStates.IsTerminal(state)) stream.Forget(job.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not publish an update for job {JobId}.", jobId);
        }
    }

    public async Task PublishApprovalAsync(string approvalId, string eventName)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
            var approval = await db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(a => a.Id == approvalId);
            if (approval is null) return;

            var described = ApprovalEndpoints.Describe(approval);
            await hub.Clients.Group(JobsHub.GroupFor(approval.UserId)).SendAsync(eventName, described);
            await stream.EmitAsync(approval.JobId, approval.UserId,
                eventName == JobsHub.ApprovalRequested ? RunEventTypes.ApprovalRequested : RunEventTypes.ApprovalResolved,
                described);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not publish approval {ApprovalId}.", approvalId);
        }
    }
}
