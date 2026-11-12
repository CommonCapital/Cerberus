using System.Text.Json;
using Cerberus.Api.Agents;
using Cerberus.Api.Auth;
using Cerberus.Api.Jobs;
using Cerberus.Data;
using Cerberus.Scanning;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Reports;

/// <summary>SARIF exports: of a scan, of a SAST agent run, and of a review (both, as separate runs in one log).</summary>
public static class SarifEndpoints
{
    public static void MapSarifEndpoints(this WebApplication app)
    {
        app.MapGet("/scans/{id}/sarif", Scan).AddEndpointFilter<RequireAuthFilter>();
        app.MapGet("/agent-runs/{id}/sarif", AgentRun).AddEndpointFilter<RequireAuthFilter>();
        app.MapGet("/reviews/{id}/sarif", Review).AddEndpointFilter<RequireAuthFilter>();
    }

    private static async Task<IResult> Scan(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        return scan is null ? NotFound("Scan") : File(Sarif.Log(RunOf(scan)), $"cerberus-scan-{Short(scan.Id)}.sarif");
    }

    private static async Task<IResult> AgentRun(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var run = await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
        if (run is null) return NotFound("Agent run");
        return run.Agent == SastAgent.Name
            ? File(Sarif.Log(RunOf(run)), $"cerberus-agent-{Short(run.Id)}.sarif")
            : Results.Json(new { error = $"A {run.Agent} run has no findings to export as SARIF." }, statusCode: 400);
    }

    private static async Task<IResult> Review(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var review = await db.Reviews.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
        if (review is null) return NotFound("Review");

        var scan = await db.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == review.ScanId && s.UserId == userId, ct);
        var agentRun = review.AgentRunId is null
            ? null
            : await db.AgentRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == review.AgentRunId && r.UserId == userId, ct);
        if (scan is null) return NotFound("The review's scan");

        // The scanner's run and the model's run side by side, never merged.
        return File(Sarif.Log(RunOf(scan), agentRun is null ? null : RunOf(agentRun)), $"cerberus-review-{Short(review.Id)}.sarif");
    }

    private static System.Text.Json.Nodes.JsonObject RunOf(Scan scan)
    {
        List<Finding> findings;
        try
        {
            findings = JsonSerializer.Deserialize<List<Finding>>(scan.Findings, TriageRunner.JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            findings = [];
        }
        return Sarif.ScanRun(scan.Id, scan.TargetPath, scan.CreatedAt, scan.ScaCompleted, findings, TriageRunner.ReadTriage(scan.Triage));
    }

    private static System.Text.Json.Nodes.JsonObject RunOf(AgentRun run) =>
        Sarif.AgentRun(run.Id, run.Model, run.TargetPath, run.CreatedAt, run.Complete, SastAgentRunner.ReadFindings(run.Findings));

    private static IResult File(string sarif, string name) =>
        Results.File(System.Text.Encoding.UTF8.GetBytes(sarif), Sarif.ContentType, name);

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    private static IResult NotFound(string what) => Results.Json(new { error = $"{what} not found" }, statusCode: 404);

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;
}
