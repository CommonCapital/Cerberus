using System.Text;
using System.Text.Json;
using Cerberus.Api.Agents;
using Cerberus.Api.Auth;
using Cerberus.Core.Models;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Web;

/// <summary>
/// The researcher's browser tool: open a page in a real browser, scroll, read
/// it as rendered — and keep a picture of it. The model gets the text (fenced,
/// like everything from outside); the picture is for the operator, stored with
/// the run as an artifact: a record of what the page looked like when the
/// agent relied on it.
/// </summary>
public sealed class BrowseTool(IAgentBrowser browser, EgressGuard guard, Func<string, string, byte[], Task<string?>> keep, IRunReporter reporter) : IAgentTool
{
    public const string Name = "browse";
    public const int MaxScreenshotsPerRun = 6;
    public const int MaxScreenshotBytes = 4_000_000;

    public ToolDefinition Definition { get; } = new(Name,
        "Open a public https page in a real browser and read it as it is rendered, after its scripts have run. " +
        "Use it when webFetch returns little or nothing useful (the page builds itself with scripts), or when what the page shows matters. " +
        "scroll is how many screens to go down before reading (0 to 10) — the text you get is the whole page's, the screenshot is of where you stopped. " +
        "A screenshot is kept for the operator as a record; you receive the text.",
        JsonDocument.Parse("""{"type":"object","properties":{"url":{"type":"string","description":"The page's address, starting with https://"},"scroll":{"type":"integer","minimum":0,"maximum":10,"description":"Screens to scroll down first; default 0"},"screenshot":{"type":"boolean","description":"Keep a screenshot for the operator; default true"}},"required":["url"],"additionalProperties":false}""").RootElement.Clone());

    public async Task<string> RunAsync(JsonElement input, CancellationToken ct)
    {
        var url = input.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(url)) throw new AgentToolException("url is required.");
        var scroll = input.TryGetProperty("scroll", out var s) && s.TryGetInt32(out var screens) ? Math.Clamp(screens, 0, ChromeBrowser.MaxScroll) : 0;
        var wanted = !input.TryGetProperty("screenshot", out var shot) || shot.ValueKind != JsonValueKind.False;
        if (guard.Refuse(url) is { } why) throw new AgentToolException($"Not opened: the address contains {why}.");

        BrowsedPage page;
        try
        {
            page = await browser.OpenAsync(url, scroll, wanted, ct);
        }
        catch (WebAccessException ex)
        {
            throw new AgentToolException($"Could not open that page: {ex.Message}.");
        }

        var text = new StringBuilder(page.Text);
        if (page.Truncated) text.Append("\n[the page is longer than what is shown]");
        if (page.Refused.Count > 0)
        {
            text.Append($"\n[the page tried to reach {page.Refused.Count} address(es) that are not allowed; those parts did not load]");
        }

        var kept = "";
        if (page.Screenshot is { Length: > 0 and <= MaxScreenshotBytes } png)
        {
            var title = page.Title.Length > 0 ? page.Title : new Uri(page.Url).Host;
            if (await keep(title, page.Url, png) is { } id)
            {
                kept = " A screenshot was kept for the operator.";
                await reporter.EventAsync(RunEventTypes.Artifact, new { id, name = title, source = page.Url, size = png.Length, contentType = "image/png" });
                await reporter.EventAsync(RunEventTypes.Note, new { message = $"Screenshot kept: {title} — {page.Url} ({page.Position})" });
            }
            else
            {
                kept = $" No screenshot was kept: this run already has {MaxScreenshotsPerRun}.";
            }
        }
        return Untrusted.Wrap($"{page.Url} — {page.Title} ({page.Position}, seen in a browser).{kept}", text.ToString());
    }
}

/// <summary>What a run left behind besides its findings: for now, the screenshots its researcher took.</summary>
public static class ArtifactEndpoints
{
    public static void MapArtifactEndpoints(this WebApplication app)
    {
        app.MapGet("/jobs/{id}/artifacts", List).AddEndpointFilter<RequireAuthFilter>();
        app.MapGet("/artifacts/{id}", Get).AddEndpointFilter<RequireAuthFilter>();
    }

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static async Task<IResult> List(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        if (!await db.Jobs.AnyAsync(j => j.Id == id && j.UserId == userId, ct)) return Results.Json(new { error = "Job not found" }, statusCode: 404);
        var artifacts = await db.RunArtifacts.AsNoTracking()
            .Where(a => a.JobId == id && a.UserId == userId)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.Name, a.ContentType, a.Source, a.Size, a.CreatedAt })
            .ToListAsync(ct);
        return Results.Json(artifacts.Select(a => new { a.Id, a.Name, a.ContentType, a.Source, a.Size, createdAt = StoredTime.Utc(a.CreatedAt) }));
    }

    private static async Task<IResult> Get(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var artifact = await db.RunArtifacts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId, ct);
        if (artifact is null) return Results.Json(new { error = "Artifact not found" }, statusCode: 404);
        // Served as a download and never as a page: it is a picture of someone else's website.
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.File(artifact.Data, artifact.ContentType, $"cerberus-{artifact.Id[..8]}.png");
    }
}
