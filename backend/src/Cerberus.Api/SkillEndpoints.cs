using System.Text.Json;
using System.Text.Json.Serialization;
using Cerberus.Api.Auth;
using Cerberus.Authorization;
using Cerberus.Scanning;
using Cerberus.Skills;

namespace Cerberus.Api;

/// <summary>
/// Picks the skills an agent should consult for a task. Today: the reference
/// procedures a triage reviewer is pointed at for a finding. Agents always
/// search at their own capability tier, so they only ever see what that tier
/// may load.
/// </summary>
public sealed class SkillAdvisor(SkillLibrary library)
{
    // Search words per scanner category, most specific first.
    private static readonly IReadOnlyDictionary<string, string[]> Queries = new Dictionary<string, string[]>
    {
        ["secret"] = ["secret", "credential"],
        ["crypto"] = ["cryptograph", "encryption"],
        ["config"] = ["tls", "hardening", "misconfiguration"],
        ["injection"] = ["injection"],
        ["deserialization"] = ["deserialization"],
    };

    public IReadOnlyList<SkillSummary> ForFinding(Finding finding, CapabilityTier agentTier, int limit = 2)
    {
        if (!Queries.TryGetValue(finding.Category, out var queries)) return [];

        var picked = new List<SkillSummary>();
        foreach (var query in queries)
        {
            foreach (var skill in library.Search(query, agentTier, limit))
            {
                if (picked.Count < limit && picked.All(p => p.Name != skill.Name)) picked.Add(skill);
            }
            if (picked.Count >= limit) break;
        }
        return picked;
    }
}

/// <summary>The skills library over HTTP: browse the catalog, read a skill.</summary>
public static class SkillEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void MapSkillEndpoints(this WebApplication app)
    {
        var skills = app.MapGroup("/skills").AddEndpointFilter<RequireAuthFilter>();
        skills.MapGet("/", List);
        skills.MapGet("/{name}", Get);
    }

    /// <summary>The whole catalog is listed, with the tier each skill needs; only the text is gated.</summary>
    private static IResult List(SkillLibrary library, string? q, string? subdomain, int? limit)
    {
        var matches = library.Search(q, CapabilityTier.T3, limit ?? 25, string.IsNullOrWhiteSpace(subdomain) ? null : subdomain);
        return Results.Json(new
        {
            total = library.Count,
            skills = matches.Select(s => new
            {
                s.Name, s.Description, s.Subdomain, s.Tags, s.MitreAttack, s.NistCsf, s.MinimumTier,
                available = SkillAccess.Allows(ScopePolicy.MaxGrantableTier, s),
            }),
        }, JsonOptions);
    }

    private static IResult Get(string name, SkillLibrary library)
    {
        try
        {
            // The API hands out what the highest tier that can currently be granted may load — no more.
            var document = library.Read(name, ScopePolicy.MaxGrantableTier);
            return document is null
                ? Results.Json(new { error = "Skill not found" }, statusCode: 404)
                : Results.Json(new
                {
                    document.Summary.Name, document.Summary.Description, document.Summary.Subdomain,
                    document.Summary.Tags, document.Summary.MinimumTier, document.Content, document.Truncated,
                }, JsonOptions);
        }
        catch (UnauthorizedAccessException)
        {
            var skill = library.Find(name)!;
            return Results.Json(new
            {
                error = $"Skill \"{skill.Name}\" is reserved for capability tier {skill.MinimumTier}, which cannot be granted yet " +
                        $"(the highest available tier is {ScopePolicy.MaxGrantableTier}).",
            }, statusCode: 403);
        }
    }
}
