using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api;
using Cerberus.Authorization;
using Cerberus.Scanning;
using Cerberus.Skills;

namespace Cerberus.Tests;

/// <summary>A small made-up skills library on disk.</summary>
public sealed class FakeSkills : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cerberus-skills-" + Guid.NewGuid().ToString("n"));

    public FakeSkills()
    {
        Add("reviewing-tls-configuration", "network-security", "Check TLS versions and certificate validation.",
            ["tls", "certificates"], "Step one: list the endpoints.\nStep two: check each one.");
        Add("detecting-hardcoded-secrets", "devsecops",
            "Find credentials committed to source\n  control and rotate them.", ["secrets", "credentials"], "Scan, then rotate.");
        Add("running-an-authorized-intrusion-exercise", "red-teaming", "Plan an authorized exercise.", ["exercise"], "RESTRICTED BODY");
        Directory.CreateDirectory(Path.Combine(Root, "not-a-skill"));
    }

    private void Add(string name, string subdomain, string description, string[] tags, string body)
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"),
            $"---\nname: {name}\ndescription: {description}\ndomain: cybersecurity\nsubdomain: {subdomain}\ntags:\n" +
            string.Concat(tags.Select(t => $"- {t}\n")) +
            "mitre_attack:\n- T1000\nnist_csf:\n- PR.DS-01\n---\n# Title\n\n" + body + "\n");
    }

    public SkillLibrary Library() => new(new SkillOptions(Root));

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class SkillLibraryTests
{
    [Fact]
    public void Index_reads_metadata_and_skips_directories_without_a_skill()
    {
        using var fake = new FakeSkills();
        var library = fake.Library();

        Assert.Equal(3, library.Count);
        var skill = library.Find("detecting-hardcoded-secrets")!;
        Assert.Equal("devsecops", skill.Subdomain);
        Assert.Equal("Find credentials committed to source control and rotate them.", skill.Description);   // folded lines joined
        Assert.Equal(["secrets", "credentials"], skill.Tags);
        Assert.Equal(["T1000"], skill.MitreAttack);
        Assert.Equal(["PR.DS-01"], skill.NistCsf);
        Assert.Equal(CapabilityTier.T0, skill.MinimumTier);
    }

    [Fact]
    public void Search_matches_every_word_and_ranks_name_hits_first()
    {
        using var fake = new FakeSkills();
        var library = fake.Library();

        Assert.Equal(["reviewing-tls-configuration"], library.Search("tls", CapabilityTier.T0).Select(s => s.Name).ToArray());
        Assert.Equal(["detecting-hardcoded-secrets"], library.Search("credentials rotate", CapabilityTier.T0).Select(s => s.Name).ToArray());
        Assert.Empty(library.Search("tls kubernetes", CapabilityTier.T0));
        Assert.Equal(2, library.Search(null, CapabilityTier.T0).Count);
        Assert.Equal(["reviewing-tls-configuration"],
            library.Search(null, CapabilityTier.T0, subdomain: "network-security").Select(s => s.Name).ToArray());
    }

    [Theory]
    [InlineData(CapabilityTier.T0, false)]
    [InlineData(CapabilityTier.T1, false)]
    [InlineData(CapabilityTier.T2, true)]
    public void Offensive_skills_are_invisible_and_unreadable_below_the_intrusive_tier(CapabilityTier tier, bool allowed)
    {
        using var fake = new FakeSkills();
        var library = fake.Library();
        const string name = "running-an-authorized-intrusion-exercise";

        Assert.Equal(CapabilityTier.T2, library.Find(name)!.MinimumTier);
        Assert.Equal(allowed, library.Search("exercise", tier).Any());
        if (allowed)
        {
            Assert.Contains("RESTRICTED BODY", library.Read(name, tier)!.Content);
        }
        else
        {
            Assert.Throws<UnauthorizedAccessException>(() => library.Read(name, tier));
        }
    }

    [Fact]
    public void Reading_returns_the_body_without_front_matter()
    {
        using var fake = new FakeSkills();

        var document = fake.Library().Read("reviewing-tls-configuration", CapabilityTier.T0)!;

        Assert.StartsWith("# Title", document.Content);
        Assert.Contains("Step two", document.Content);
        Assert.DoesNotContain("subdomain:", document.Content);
        Assert.False(document.Truncated);
    }

    [Theory]
    [InlineData("no-such-skill")]
    [InlineData("../detecting-hardcoded-secrets")]
    [InlineData("/etc/passwd")]
    [InlineData("not-a-skill")]
    public void Names_are_looked_up_never_used_as_paths(string name)
    {
        using var fake = new FakeSkills();

        Assert.Null(fake.Library().Read(name, CapabilityTier.T3));
    }

    [Fact]
    public void A_missing_library_is_simply_empty()
    {
        var library = new SkillLibrary(new SkillOptions(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))));

        Assert.Equal(0, library.Count);
        Assert.Empty(library.Search("anything", CapabilityTier.T3));
    }

    [Theory]
    [InlineData("red-teaming", CapabilityTier.T2)]
    [InlineData("Penetration-Testing", CapabilityTier.T2)]
    [InlineData("offensive-security", CapabilityTier.T2)]
    [InlineData("cloud-security", CapabilityTier.T0)]
    [InlineData("incident-response", CapabilityTier.T0)]
    public void Tier_follows_the_subdomain(string subdomain, CapabilityTier expected)
    {
        Assert.Equal(expected, SkillAccess.MinimumTierFor(subdomain));
    }
}

public class VendoredSkillLibraryTests
{
    private static SkillLibrary Vendored()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "security-skills"))) dir = dir.Parent;
        return new SkillLibrary(new SkillOptions(Path.Combine(dir!.FullName, "security-skills", "skills")));
    }

    [Fact]
    public void The_vendored_library_loads_with_complete_metadata()
    {
        var library = Vendored();

        Assert.Equal(818, library.Count);
        Assert.All(library.All, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Name));
            Assert.False(string.IsNullOrWhiteSpace(s.Description), $"{s.Name} has no description");
            Assert.False(string.IsNullOrWhiteSpace(s.Subdomain));
        });
        // Some are reserved for the intrusive tiers, most are not.
        var restricted = library.All.Count(s => s.MinimumTier > CapabilityTier.T0);
        Assert.InRange(restricted, 40, 120);
    }

    [Theory]
    [InlineData("secret")]
    [InlineData("crypto")]
    [InlineData("config")]
    [InlineData("injection")]
    [InlineData("deserialization")]
    public void The_advisor_finds_passive_tier_skills_for_every_scanner_category(string category)
    {
        var finding = new Finding("id", "title", Severity.Medium, category, null, "a.py", 1, "e", "low");

        var skills = new SkillAdvisor(Vendored()).ForFinding(finding, CapabilityTier.T0);

        Assert.NotEmpty(skills);
        Assert.All(skills, s => Assert.Equal(CapabilityTier.T0, s.MinimumTier));
        Assert.True(skills.Count <= 2);
    }

    [Fact]
    public void The_advisor_offers_nothing_for_categories_it_has_no_guidance_on()
    {
        var finding = new Finding("id", "title", Severity.High, "dependency", null, "a.py", 1, "e", "high");

        Assert.Empty(new SkillAdvisor(Vendored()).ForFinding(finding, CapabilityTier.T0));
    }
}

[Collection("api")]
public class SkillApiTests(ApiHost host)
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task The_catalog_can_be_searched_and_an_available_skill_read()
    {
        var (client, _) = await host.NewUserAsync();

        var page = await client.GetFromJsonAsync<JsonElement>("/skills?q=tls&limit=5");
        Assert.Equal(818, page.GetProperty("total").GetInt32());
        var skills = page.GetProperty("skills").EnumerateArray().ToList();
        Assert.InRange(skills.Count, 1, 5);

        var available = skills.First(s => s.GetProperty("available").GetBoolean());
        var name = available.GetProperty("name").GetString();
        var document = await client.GetFromJsonAsync<JsonElement>($"/skills/{name}");
        Assert.Equal(name, document.GetProperty("name").GetString());
        Assert.True(document.GetProperty("content").GetString()!.Length > 200);
    }

    [Fact]
    public async Task Skills_reserved_for_intrusive_tiers_are_listed_but_not_handed_out()
    {
        var (client, _) = await host.NewUserAsync();

        var page = await client.GetFromJsonAsync<JsonElement>("/skills?subdomain=red-teaming&limit=3");
        var skill = page.GetProperty("skills").EnumerateArray().First();
        Assert.Equal("T2", skill.GetProperty("minimumTier").GetString());
        Assert.False(skill.GetProperty("available").GetBoolean());

        var refused = await client.GetAsync($"/skills/{skill.GetProperty("name").GetString()}");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("cannot be granted yet", (await Json(refused)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Unknown_skills_and_anonymous_callers_get_nothing()
    {
        var (client, _) = await host.NewUserAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/skills/no-such-skill")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/skills")).StatusCode);
    }

    [Fact]
    public async Task The_triage_reviewer_is_pointed_at_skills_and_the_result_says_which()
    {
        var (client, _) = await host.NewUserAsync();
        var scan = await Json(await client.PostAsJsonAsync("/scan", new { path = "svc", sca = false }));
        var scanId = scan.GetProperty("id").GetString();
        var sentBefore = host.Model.Requests.Count;

        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "triage", scanId, model = "echo" }));
        var jobId = submitted.GetProperty("id").GetString();
        for (var i = 0; i < 200; i++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}");
            if (job.GetProperty("state").GetString() is "Succeeded" or "Failed") break;
            await Task.Delay(50);
        }

        var opinion = (await client.GetFromJsonAsync<JsonElement>($"/scans/{scanId}")).GetProperty("triage").EnumerateArray().Single();
        var named = opinion.GetProperty("skills").EnumerateArray().Select(s => s.GetString()!).ToList();
        Assert.InRange(named.Count, 1, 2);

        // The same names were in what the model was sent, under the reference heading.
        var prompt = host.Model.Requests.Skip(sentBefore).Last().Messages.Single().Content;
        Assert.Contains("Reference procedures from the platform's skills library", prompt);
        Assert.All(named, name => Assert.Contains($"- {name}: ", prompt));
    }
}
