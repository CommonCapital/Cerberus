using System.Text.Json;
using Cerberus.Api.Auth;
using Cerberus.Data;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Agents;

/// <summary>Reading what agent runs produced: their summary and the findings they reported.</summary>
public static class AgentRunEndpoints
{
    public static void MapAgentRunEndpoints(this WebApplication app)
    {
        var runs = app.MapGroup("/agent-runs").AddEndpointFilter<RequireAuthFilter>();
        runs.MapGet("/", List);
        runs.MapGet("/{id}", Get);
        runs.MapGet("/{id}/report", Report);
    }

    /// <summary>The run's written report, as Markdown. Only some agents write one.</summary>
    private static async Task<IResult> Report(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var run = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
        if (run is null) return Results.Json(new { error = "Agent run not found" }, statusCode: 404);
        return run.Report.Length == 0
            ? Results.Json(new { error = "This agent run has no report" }, statusCode: 404)
            : Results.Text(run.Report, "text/markdown; charset=utf-8");
    }

    private static async Task<IResult> List(HttpContext http, CerberusDbContext db, int? limit, CancellationToken ct)
    {
        var userId = UserId(http);
        var rows = await db.AgentRuns.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(limit ?? 50, 1, 200))
            .ToListAsync(ct);
        return Results.Json(rows.Select(r => Describe(r, withFindings: false)));
    }

    private static async Task<IResult> Get(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var run = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
        if (run is null) return Results.Json(new { error = "Agent run not found" }, statusCode: 404);
        // A lead's run comes with the runs of the specialists it sent.
        var specialists = run.Agent == Team.Name
            ? await db.AgentRuns.AsNoTracking().Where(r => r.ParentRunId == run.Id && r.UserId == userId).OrderBy(r => r.CreatedAt).ToListAsync(ct)
            : [];
        return Results.Json(Describe(run, withFindings: true, specialists));
    }

    private static object Describe(AgentRun r, bool withFindings, IReadOnlyList<AgentRun>? specialists = null) => new
    {
        id = r.Id,
        jobId = r.JobId,
        parentRunId = r.ParentRunId,
        engagementId = r.EngagementId,
        agent = r.Agent,
        model = r.Model,
        targetPath = r.TargetPath,
        complete = r.Complete,
        steps = r.Steps,
        toolCalls = r.ToolCalls,
        findingCount = r.FindingCount,
        summary = r.Summary,
        createdAt = r.CreatedAt,
        hasReport = r.Report.Length > 0,
        findings = withFindings ? JsonSerializer.Deserialize<JsonElement>(r.Findings) : (JsonElement?)null,
        report = withFindings && r.Report.Length > 0 ? r.Report : null,
        specialists = specialists is { Count: > 0 }
            ? specialists.Select(s => new
            {
                id = s.Id, agent = s.Agent, complete = s.Complete, steps = s.Steps, toolCalls = s.ToolCalls, findingCount = s.FindingCount,
                summary = s.Summary, hasReport = s.Report.Length > 0,
            })
            : null,
    };

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;
}
