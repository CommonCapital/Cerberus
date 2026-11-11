using System.Text.Json;
using Cerberus.Api.Auth;
using Cerberus.Data;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Jobs;

/// <summary>Reading the outcome of review jobs: what each produced, and its report.</summary>
public static class ReviewEndpoints
{
    public static void MapReviewEndpoints(this WebApplication app)
    {
        var reviews = app.MapGroup("/reviews").AddEndpointFilter<RequireAuthFilter>();
        reviews.MapGet("/", List);
        reviews.MapGet("/{id}", Get);
        reviews.MapGet("/{id}/report", Report);
    }

    private static async Task<IResult> List(HttpContext http, CerberusDbContext db, int? limit, CancellationToken ct)
    {
        var userId = UserId(http);
        var rows = await db.Reviews.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(Math.Clamp(limit ?? 50, 1, 200))
            .ToListAsync(ct);
        return Results.Json(rows.Select(r => Describe(r, withReport: false)));
    }

    private static async Task<IResult> Get(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var review = await FindAsync(db, UserId(http), id, ct);
        return review is null ? NotFound() : Results.Json(Describe(review, withReport: true));
    }

    /// <summary>The report itself, as Markdown.</summary>
    private static async Task<IResult> Report(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var review = await FindAsync(db, UserId(http), id, ct);
        return review is null ? NotFound() : Results.Text(review.Report, "text/markdown; charset=utf-8");
    }

    private static Task<Review?> FindAsync(CerberusDbContext db, string userId, string id, CancellationToken ct) =>
        db.Reviews.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);

    private static object Describe(Review r, bool withReport) => new
    {
        id = r.Id,
        jobId = r.JobId,
        engagementId = r.EngagementId,
        targetPath = r.TargetPath,
        scanId = r.ScanId,
        agentRunId = r.AgentRunId,
        assessmentIds = JsonSerializer.Deserialize<List<string>>(r.AssessmentIds) ?? [],
        findingCount = r.FindingCount,
        createdAt = r.CreatedAt,
        report = withReport ? r.Report : null,
    };

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static IResult NotFound() => Results.Json(new { error = "Review not found" }, statusCode: 404);
}
