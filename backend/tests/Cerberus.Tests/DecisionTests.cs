using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api;
using Cerberus.Scanning;

namespace Cerberus.Tests;

public class FindingFingerprintTests
{
    private static Finding Code(string file, int line, string evidence, string rule = "injection.os-command") =>
        new($"{rule}:{file}:{line}", "Title", Severity.High, "injection", null, file, line, evidence, "medium");

    [Fact]
    public void The_same_code_in_the_same_file_is_the_same_finding_wherever_the_line_moved_to()
    {
        var before = FindingDecisions.Fingerprint(Code("app/a.py", 10, "os.system(cmd)"));

        Assert.Equal(before, FindingDecisions.Fingerprint(Code("app/a.py", 42, "os.system(cmd)")));
        Assert.Equal(before, FindingDecisions.Fingerprint(Code("app/a.py", 42, "os.system(cmd)   ")));       // spacing does not matter
        Assert.Equal(before, FindingDecisions.Fingerprint(Code("app/a.py", 42, "os.system(cmd)\t")));
        Assert.Equal(32, before.Length);
    }

    [Fact]
    public void Other_code_another_file_or_another_rule_is_another_finding()
    {
        var one = FindingDecisions.Fingerprint(Code("app/a.py", 10, "os.system(cmd)"));

        Assert.NotEqual(one, FindingDecisions.Fingerprint(Code("app/a.py", 10, "os.system(other)")));
        Assert.NotEqual(one, FindingDecisions.Fingerprint(Code("app/b.py", 10, "os.system(cmd)")));
        Assert.NotEqual(one, FindingDecisions.Fingerprint(Code("app/a.py", 10, "os.system(cmd)", rule: "injection.eval")));
    }

    [Fact]
    public void A_dependency_finding_is_the_same_one_for_the_same_advisory_package_and_version()
    {
        Finding Dependency(string version, int line) => new($"sca.npm:lodash@{version}:GHSA-x", "lodash", Severity.High, "dependency", null,
            "package.json", line, $"lodash {version}", "high", Package: "lodash", Version: version, VulnId: "GHSA-x");

        Assert.Equal(FindingDecisions.Fingerprint(Dependency("4.17.0", 5)), FindingDecisions.Fingerprint(Dependency("4.17.0", 9)));
        Assert.NotEqual(FindingDecisions.Fingerprint(Dependency("4.17.0", 5)), FindingDecisions.Fingerprint(Dependency("4.17.1", 5)));
    }
}

[Collection("api")]
public class FindingDecisionApiTests(ApiHost host)
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>A fresh directory under the scan root with one file that the scanner flags.</summary>
    private static (string Path, string File) Plant()
    {
        var name = "decide-" + Guid.NewGuid().ToString("n")[..8];
        var dir = Path.Combine(Environment.GetEnvironmentVariable("CERBERUS_SCAN_ROOT")!, name);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "run.py");
        File.WriteAllText(file, "import os\nos.system(cmd)\nimport pickle\nobj = pickle.loads(blob)\n");
        return (name, file);
    }

    private static async Task<JsonElement> ScanAsync(HttpClient client, string path) =>
        await client.GetFromJsonAsync<JsonElement>(
            $"/scans/{(await Json(await client.PostAsJsonAsync("/scan", new { path, sca = false }))).GetProperty("id").GetString()}");

    private static JsonElement Finding(JsonElement scan, string rule) =>
        scan.GetProperty("findings").EnumerateArray().Single(f => f.GetProperty("id").GetString()!.StartsWith(rule + ":"));

    [Fact]
    public async Task A_decision_is_recorded_with_its_reason_and_follows_the_finding_into_later_scans_even_when_the_line_moves()
    {
        var (client, _) = await host.NewUserAsync();
        var (path, file) = Plant();
        var scan = await ScanAsync(client, path);
        var command = Finding(scan, "injection.os-command");
        Assert.Equal(JsonValueKind.Null, command.GetProperty("decision").ValueKind);
        Assert.Equal(2, command.GetProperty("line").GetInt32());

        var decided = await client.PutAsJsonAsync($"/scans/{scan.GetProperty("id").GetString()}/findings/decision",
            new { findingId = command.GetProperty("id").GetString(), status = "accepted-risk", note = " Internal tool, cmd is a constant. " });
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);

        // On this scan: that finding has the decision, the other is still open.
        var again = await client.GetFromJsonAsync<JsonElement>($"/scans/{scan.GetProperty("id").GetString()}");
        var decision = Finding(again, "injection.os-command").GetProperty("decision");
        Assert.Equal(("accepted-risk", "Internal tool, cmd is a constant."), (decision.GetProperty("status").GetString(), decision.GetProperty("note").GetString()));
        Assert.Equal(JsonValueKind.Null, Finding(again, "deserialization.unsafe").GetProperty("decision").ValueKind);

        // The code moves down three lines; a new scan finds it at another line and still knows the decision.
        File.WriteAllText(file, "# header\n# more\n# and more\n" + File.ReadAllText(file));
        var later = await ScanAsync(client, path);
        var moved = Finding(later, "injection.os-command");
        Assert.Equal(5, moved.GetProperty("line").GetInt32());
        Assert.Equal("accepted-risk", moved.GetProperty("decision").GetProperty("status").GetString());
        Assert.Equal(command.GetProperty("fingerprint").GetString(), moved.GetProperty("fingerprint").GetString());

        // Changed code is a new finding: the old decision does not cover it.
        File.WriteAllText(file, File.ReadAllText(file).Replace("os.system(cmd)", "os.system(cmd + user_input)"));
        var changed = await ScanAsync(client, path);
        Assert.Equal(JsonValueKind.Null, Finding(changed, "injection.os-command").GetProperty("decision").ValueKind);

        var actions = (await client.GetFromJsonAsync<JsonElement>("/audit?limit=50")).EnumerateArray().ToList();
        var entry = actions.Single(e => e.GetProperty("action").GetString() == "finding.decide");
        Assert.Contains("accepted risk: Internal tool, cmd is a constant.", entry.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Decisions_show_in_the_review_report_and_the_sarif_export_and_can_be_withdrawn()
    {
        var (client, _) = await host.NewUserAsync();
        await client.PutAsJsonAsync("/settings/permissions", new { allowedKinds = new[] { "run-plan" } });
        var (path, _) = Plant();
        var scan = await ScanAsync(client, path);
        var scanId = scan.GetProperty("id").GetString();
        await client.PutAsJsonAsync($"/scans/{scanId}/findings/decision",
            new { findingId = Finding(scan, "deserialization.unsafe").GetProperty("id").GetString(), status = "false-positive", note = "blob is produced by us | never external" });

        // A review of the same code: the decision is in its report, and open findings come first.
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "review", path, sca = false, catalogs = new[] { "owasp-top10" } }));
        JsonElement job = default;
        for (var i = 0; i < 400; i++)
        {
            job = await client.GetFromJsonAsync<JsonElement>($"/jobs/{submitted.GetProperty("id").GetString()}");
            if (job.GetProperty("state").GetString() is "Succeeded" or "Failed") break;
            await Task.Delay(40);
        }
        var report = await client.GetStringAsync($"/reviews/{job.GetProperty("resultId").GetString()}/report");
        Assert.Contains("- **Your decisions:** 1 of the 2 production findings already have one (0 accepted risk, 1 false positive); 1 still open", report);
        Assert.Contains("| open |", report);
        Assert.Contains("| false positive: blob is produced by us \\| never external |", report);
        Assert.True(report.IndexOf("| open |", StringComparison.Ordinal) < report.IndexOf("| false positive:", StringComparison.Ordinal));

        // In SARIF: an external suppression with the reason.
        var sarif = JsonDocument.Parse(await client.GetStringAsync($"/scans/{scanId}/sarif")).RootElement;
        var results = sarif.GetProperty("runs")[0].GetProperty("results").EnumerateArray().ToList();
        var suppressed = results.Single(r => r.GetProperty("ruleId").GetString() == "deserialization.unsafe").GetProperty("suppressions")[0];
        Assert.Equal(("external", "False positive: blob is produced by us | never external"),
            (suppressed.GetProperty("kind").GetString(), suppressed.GetProperty("justification").GetString()));
        Assert.False(results.Single(r => r.GetProperty("ruleId").GetString() == "injection.os-command").TryGetProperty("suppressions", out _));

        // Withdrawn: the finding is open again.
        var listed = await client.GetFromJsonAsync<JsonElement>("/decisions");
        var decisionId = Assert.Single(listed.EnumerateArray()).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/decisions/{decisionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/decisions/{decisionId}")).StatusCode);
        var open = await client.GetFromJsonAsync<JsonElement>($"/scans/{scanId}");
        Assert.Equal(JsonValueKind.Null, Finding(open, "deserialization.unsafe").GetProperty("decision").ValueKind);
    }

    [Fact]
    public async Task A_decision_needs_a_known_status_a_reason_and_a_finding_of_ones_own_scan()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();
        var (path, _) = Plant();
        var scan = await ScanAsync(client, path);
        var url = $"/scans/{scan.GetProperty("id").GetString()}/findings/decision";
        var findingId = Finding(scan, "injection.os-command").GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new { findingId, status = "fixed", note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new { findingId, status = "accepted-risk", note = "  " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new { findingId, status = "accepted-risk", note = new string('x', 2001) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(url, new { findingId = "nope:x:1", status = "accepted-risk", note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync(url, new { findingId, status = "accepted-risk", note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/decisions")).StatusCode);

        // One user's decision is not another's: the same code scanned by someone else is open.
        await client.PutAsJsonAsync(url, new { findingId, status = "accepted-risk", note = "mine" });
        var theirs = await ScanAsync(other, path);
        Assert.Equal(JsonValueKind.Null, Finding(theirs, "injection.os-command").GetProperty("decision").ValueKind);
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/decisions")).GetArrayLength());
    }
}
