using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api.Agents;
using Cerberus.Api.Reports;
using Cerberus.Scanning;

namespace Cerberus.Tests;

public class SarifWriterTests
{
    private static readonly DateTime When = new(2026, 11, 12, 9, 30, 0, DateTimeKind.Utc);

    private static Finding Static(string rule, string file, int line, Severity severity, string evidence = "x", string? context = null, bool suppressed = false) =>
        new($"{rule}:{file}:{line}", $"Title of {rule}", severity, rule.Split('.')[0], "CWE-327", file, line, evidence, "medium",
            Owasp: "A02", Remediation: "Fix it.", Context: context, Suppressed: suppressed);

    private static JsonElement Parse(string log) => JsonDocument.Parse(log).RootElement;

    [Fact]
    public void A_scan_becomes_one_run_with_a_rule_per_rule_and_a_result_per_finding()
    {
        List<Finding> findings =
        [
            Static("crypto.weak-hash", "app/a b.py", 3, Severity.Medium),
            Static("crypto.weak-hash", "app/c.py", 9, Severity.Medium, context: "test"),
            Static("injection.os-command", "app/c.py", 12, Severity.High, suppressed: true),
        ];

        var log = Parse(Sarif.Log(Sarif.ScanRun("scan-1", "app", When, dependencyCheckCompleted: false, findings, [])));

        Assert.Equal("2.1.0", log.GetProperty("version").GetString());
        var run = Assert.Single(log.GetProperty("runs").EnumerateArray());
        Assert.Equal("Cerberus", run.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString());
        Assert.Equal("cerberus/scan/scan-1", run.GetProperty("automationDetails").GetProperty("id").GetString());
        Assert.Equal("2026-11-12T09:30:00Z", run.GetProperty("invocations")[0].GetProperty("endTimeUtc").GetString());
        Assert.False(run.GetProperty("properties").GetProperty("dependencyCheckCompleted").GetBoolean());

        var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray().ToList();
        Assert.Equal(["crypto.weak-hash", "injection.os-command"], rules.Select(r => r.GetProperty("id").GetString()!).ToArray());
        Assert.Equal("warning", rules[0].GetProperty("defaultConfiguration").GetProperty("level").GetString());
        Assert.Equal("8.0", rules[1].GetProperty("properties").GetProperty("security-severity").GetString());
        Assert.Equal(["security", "crypto", "external/cwe/cwe-327", "owasp-top10-2021/A02"],
            rules[0].GetProperty("properties").GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray());
        Assert.Equal("Fix it.", rules[0].GetProperty("help").GetProperty("text").GetString());

        var results = run.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal([0, 0, 1], results.Select(r => r.GetProperty("ruleIndex").GetInt32()).ToArray());
        var location = results[0].GetProperty("locations")[0].GetProperty("physicalLocation");
        Assert.Equal("app/a%20b.py", location.GetProperty("artifactLocation").GetProperty("uri").GetString());   // a valid URI reference
        Assert.Equal(3, location.GetProperty("region").GetProperty("startLine").GetInt32());
        Assert.Equal("crypto.weak-hash:app/a b.py:3", results[0].GetProperty("partialFingerprints").GetProperty("cerberusFindingId/v1").GetString());
        Assert.Equal("test", results[1].GetProperty("properties").GetProperty("context").GetString());

        // What the target asked to hide is exported, marked as suppressed in the source.
        Assert.False(results[0].TryGetProperty("suppressions", out _));
        Assert.Equal("inSource", results[2].GetProperty("suppressions")[0].GetProperty("kind").GetString());
        Assert.Equal("error", results[2].GetProperty("level").GetString());
    }

    [Fact]
    public void Credentials_in_a_snippet_are_masked_in_the_export()
    {
        var secret = Static("secret.aws-access-key", "app/settings.py", 4, Severity.High, evidence: "AWS_KEY = \"AKIAIOSFODNN7EXAMPLE\"");

        var text = Sarif.Log(Sarif.ScanRun("scan-1", "app", When, true, [secret], []));

        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", text);
        Assert.Contains("AWS_KEY", text);
    }

    [Fact]
    public void A_dependency_finding_is_filed_under_its_advisory_and_a_models_opinion_rides_along_as_a_property()
    {
        var dependency = new Finding("sca.npm:lodash@4.17.0:GHSA-xxxx", "lodash: prototype pollution", Severity.High, "dependency", "CWE-1321",
            "package.json", 12, "lodash 4.17.0", "high", Package: "lodash", Version: "4.17.0", FixedVersion: "4.17.21",
            VulnId: "GHSA-xxxx", Reference: "https://osv.dev/vulnerability/GHSA-xxxx");
        var code = Static("crypto.weak-hash", "a.py", 1, Severity.Medium);
        var opinion = new Cerberus.Triage.TriageResult(code.Id, Cerberus.Triage.TriageVerdict.FalsePositive, "Only a cache key.", null, "llama3.1", When);

        var run = Parse(Sarif.Log(Sarif.ScanRun("s", ".", When, true, [dependency, code], [opinion]))).GetProperty("runs")[0];

        var rule = run.GetProperty("tool").GetProperty("driver").GetProperty("rules")[0];
        Assert.Equal("GHSA-xxxx", rule.GetProperty("id").GetString());
        Assert.Equal("https://osv.dev/vulnerability/GHSA-xxxx", rule.GetProperty("helpUri").GetString());
        var first = run.GetProperty("results")[0];
        Assert.Equal("lodash: prototype pollution (lodash 4.17.0; fixed in 4.17.21)", first.GetProperty("message").GetProperty("text").GetString());
        Assert.Equal("4.17.21", first.GetProperty("properties").GetProperty("fixedVersion").GetString());

        var review = run.GetProperty("results")[1].GetProperty("properties").GetProperty("modelReview");
        Assert.Equal(("FalsePositive", "llama3.1"), (review.GetProperty("verdict").GetString(), review.GetProperty("model").GetString()));
        Assert.Equal("warning", run.GetProperty("results")[1].GetProperty("level").GetString());   // the opinion changed nothing
    }

    [Fact]
    public void A_models_findings_are_their_own_run_named_for_the_model_with_paths_from_the_scan_root()
    {
        List<AgentFinding> findings =
        [
            new("agent.sast:auth.py:12", "Plain-text password comparison", Severity.High, "auth", "CWE-256", "auth.py", 12, "if pw == stored:", "Passwords are compared as stored.", "Hash them."),
            new("agent.sast:db.py:3", "Query built from input", Severity.Critical, "injection", null, "db.py", 3, "q = 'select ' + name", "name comes from the request.", null),
        ];

        var log = Parse(Sarif.Log(
            Sarif.ScanRun("s", "svc", When, true, [], []),
            Sarif.AgentRun("run-1", "llama3.1", "./svc/", When, complete: false, findings),
            null));

        var runs = log.GetProperty("runs").EnumerateArray().ToList();
        Assert.Equal(2, runs.Count);   // never merged; a missing run is simply left out
        var model = runs[1];
        Assert.Equal("Cerberus SAST agent (llama3.1)", model.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString());
        Assert.False(model.GetProperty("properties").GetProperty("complete").GetBoolean());
        Assert.Contains("not confirmed vulnerabilities", model.GetProperty("properties").GetProperty("note").GetString());
        Assert.Equal(["model.auth", "model.injection"],
            model.GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray());

        var result = model.GetProperty("results")[0];
        Assert.Equal("svc/auth.py", result.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.Equal("Plain-text password comparison: Passwords are compared as stored.", result.GetProperty("message").GetProperty("text").GetString());
        Assert.Equal(("model", "CWE-256"), (result.GetProperty("properties").GetProperty("source").GetString(), result.GetProperty("properties").GetProperty("cwe").GetString()));
        Assert.Equal("error", model.GetProperty("results")[1].GetProperty("level").GetString());
    }
}

[Collection("api")]
public class SarifApiTests(ApiHost host)
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> WaitForEndAsync(HttpClient client, string jobId)
    {
        for (var i = 0; i < 400; i++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}");
            if (job.GetProperty("state").GetString() is "Succeeded" or "Failed" or "Cancelled" or "TimedOut") return job;
            await Task.Delay(40);
        }
        throw new TimeoutException("job did not finish");
    }

    private static async Task<JsonElement> SarifAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/sarif+json", response.Content.Headers.ContentType!.MediaType);
        Assert.EndsWith(".sarif", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task A_scan_is_exported_with_its_suppressed_finding_marked()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();
        var scan = await Json(await client.PostAsJsonAsync("/scan", new { path = "svc", sca = false }));
        var scanId = scan.GetProperty("id").GetString();

        var log = await SarifAsync(client, $"/scans/{scanId}/sarif");

        var run = Assert.Single(log.GetProperty("runs").EnumerateArray());
        var results = run.GetProperty("results").EnumerateArray().ToList();
        var pickle = results.Single(r => r.GetProperty("ruleId").GetString() == "deserialization.unsafe");
        Assert.Equal("svc/loader.py", pickle.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.False(pickle.TryGetProperty("suppressions", out _));
        // The exact severity travels with each result: "error" alone cannot tell critical from high.
        Assert.Contains(pickle.GetProperty("properties").GetProperty("severity").GetString(), new[] { "critical", "high", "medium", "low", "info" });
        var hidden = results.Single(r => r.GetProperty("ruleId").GetString() == "crypto.weak-hash");
        Assert.Equal("inSource", hidden.GetProperty("suppressions")[0].GetProperty("kind").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/scans/{scanId}/sarif")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync($"/scans/{scanId}/sarif")).StatusCode);
    }

    [Fact]
    public async Task A_review_exports_the_scanner_and_the_model_as_two_runs_and_a_threat_model_has_nothing_to_export()
    {
        var (client, _) = await host.NewUserAsync();
        await client.PutAsJsonAsync("/settings/permissions", new { allowedKinds = new[] { "run-plan" } });
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new
        {
            type = "review", path = "svc", sca = false, exclusions = "honour", catalogs = new[] { "owasp-top10" },
            agentModel = "llama3.1", agents = new[] { "sast", "threat-model" },
        }));
        var job = await WaitForEndAsync(client, submitted.GetProperty("id").GetString()!);
        var review = await client.GetFromJsonAsync<JsonElement>($"/reviews/{job.GetProperty("resultId").GetString()}");

        var log = await SarifAsync(client, $"/reviews/{review.GetProperty("id").GetString()}/sarif");
        var runs = log.GetProperty("runs").EnumerateArray().ToList();
        Assert.Equal(["Cerberus", "Cerberus SAST agent (llama3.1)"],
            runs.Select(r => r.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString()!).ToArray());
        // Both point at the same file in the same way, so a reader can line them up.
        var modelResult = Assert.Single(runs[1].GetProperty("results").EnumerateArray());
        Assert.Equal("svc/loader.py", modelResult.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString());

        // The SAST run on its own; the threat model is not a list of findings.
        var alone = await SarifAsync(client, $"/agent-runs/{review.GetProperty("agentRunId").GetString()}/sarif");
        Assert.Single(alone.GetProperty("runs").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/agent-runs/{review.GetProperty("threatModelRunId").GetString()}/sarif")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/reviews/no-such/sarif")).StatusCode);
    }
}
