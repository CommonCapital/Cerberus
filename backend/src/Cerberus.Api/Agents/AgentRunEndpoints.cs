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
        return run is null
            ? Results.Json(new { error = "Agent run not found" }, statusCode: 404)
            : Results.Json(Describe(run, withFindings: true));
    }

    private static object Describe(AgentRun r, bool withFindings) => new
    {
        id = r.Id,
        jobId = r.JobId,
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
        findings = withFindings ? JsonSerializer.Deserialize<JsonElement>(r.Findings) : (JsonElement?)null,
    };

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;
}
