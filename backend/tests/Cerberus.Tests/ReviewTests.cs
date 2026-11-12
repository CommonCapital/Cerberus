using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api.Jobs;
using Cerberus.Compliance;

namespace Cerberus.Tests;

public class ReviewPlanTests
{
    private static CatalogLoader Catalogs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "catalogs"))) dir = dir.Parent;
        return new CatalogLoader(new CatalogOptions(Path.Combine(dir!.FullName, "catalogs")));
    }

    [Fact]
    public void The_plan_lists_scan_assessments_and_report_in_order()
    {
        var plan = ReviewPlan.Build("app", new ReviewJobParameters(["owasp-top10", "enterprise-sap"]), Catalogs());

        Assert.Equal(["scan", "assess:owasp-top10", "assess:enterprise-sap", "report"], plan.Select(s => s.Id).ToArray());
        Assert.Contains("OSV.dev", plan[0].Detail);
    }

    [Fact]
    public void Model_review_is_a_step_only_when_asked_for_and_says_where_the_code_goes()
    {
        var local = ReviewPlan.Build("app", new ReviewJobParameters(["owasp-top10"], Sca: false, TriageModel: "echo"), Catalogs());
        var cloud = ReviewPlan.Build("app", new ReviewJobParameters(["owasp-top10"], TriageModel: "claude-haiku-4-5"), Catalogs());

        Assert.Equal(["scan", "triage", "assess:owasp-top10", "report"], local.Select(s => s.Id).ToArray());
        Assert.Contains("No dependency check", local[0].Detail);
        Assert.Contains("no code leaves this machine", local[1].Detail);
        Assert.Contains("is sent to Anthropic", cloud[1].Detail);
    }

    [Fact]
    public void With_a_model_the_review_dispatches_the_sast_agent_alone_unless_other_agents_are_named()
    {
        var byDefault = ReviewPlan.Build("app", new ReviewJobParameters(["owasp-top10"], AgentModel: "llama3.1"), Catalogs());
        var all = ReviewPlan.Build("app",
            new ReviewJobParameters(["owasp-top10", "enterprise-sap"], AgentModel: "llama3.1", Agents: ["sast", "threat-model", "evidence"]), Catalogs());
        var none = new ReviewJobParameters(["owasp-top10"], Agents: ["sast"]);   // no model: nothing to run them

        Assert.Equal(["scan", "sast", "assess:owasp-top10", "report"], byDefault.Select(s => s.Id).ToArray());
        Assert.Equal(
            ["scan", "sast", "threat-model", "assess:owasp-top10", "evidence:owasp-top10", "assess:enterprise-sap", "evidence:enterprise-sap", "report"],
            all.Select(s => s.Id).ToArray());
        Assert.All(all.Where(s => s.Id is "sast" or "threat-model" || s.Id.StartsWith("evidence:")), s => Assert.Contains("no code leaves this machine", s.Detail));
        Assert.Empty(none.AgentsToRun);
    }

    [Fact]
    public void The_report_says_for_each_model_finding_what_the_scanner_saw_at_that_place()
    {
        Cerberus.Scanning.Finding Scanner(string file, int line, string? context = null, bool suppressed = false) =>
            new($"rule:{file}:{line}", "Weak hash", Cerberus.Scanning.Severity.Medium, "crypto", null, file, line, "x", "medium",
                Context: context, Suppressed: suppressed);
        Cerberus.Api.Agents.AgentFinding Model(string file, int line) =>
            new($"agent.sast:{file}:{line}", $"Problem in {file}", Cerberus.Scanning.Severity.High, "other", null, file, line, "x", "Because.", null);

        List<Cerberus.Scanning.Finding> findings =
        [
            Scanner("svc/a.py", 10),                       // production
            Scanner("svc/tests/t.py", 5, context: "test"), // test code
            Scanner("svc/hidden.py", 7, suppressed: true), // the target marked it to be ignored
        ];
        var scan = new ScanRun(new Cerberus.Data.Scan { Id = "scan-1" },
            new Cerberus.Scanning.ScanResult(3, findings), new Cerberus.Scanning.ScaResult(0, 0, [], false), findings);
        var agentRun = new Cerberus.Data.AgentRun
        {
            Model = "llama3.1", Complete = true,
            Findings = JsonSerializer.Serialize(new[] { Model("a.py", 11), Model("tests/t.py", 5), Model("hidden.py", 7), Model("new.py", 1) },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }),
        };

        var report = ReviewReport.Write("svc", scan, [], [], DateTime.UtcNow, agentRun);

        Assert.Contains("- **Reported by the model:** 4 (3 at places the scanner also flagged, 1 the model's alone)", report);
        Assert.Contains("| `svc/a.py:11` | flagged it too |", report);                 // a line apart still counts as the same place
        Assert.Contains("| `svc/tests/t.py:5` | flagged it too |", report);           // also when the scanner filed it under tests
        Assert.Contains("| `svc/hidden.py:7` | flagged it, but the target suppressed it |", report);
        Assert.Contains("| `svc/new.py:1` | model only |", report);
        Assert.Contains("| `svc/a.py:10` | — | yes |", report);                        // and the scanner's table says so of the model
    }

    [Fact]
    public void The_description_is_a_numbered_list()
    {
        var plan = ReviewPlan.Build("app", new ReviewJobParameters(["owasp-top10"]), Catalogs());

        var lines = ReviewPlan.Describe(plan).Split('\n');

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("1. Scan \"app\"", lines[0]);
        Assert.StartsWith("3. Write the report", lines[2]);
    }
}

[Collection("api")]
public class ReviewApiTests(ApiHost host)
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

    private static async Task<List<JsonElement>> EventsAsync(HttpClient client, string jobId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}/events?after=0")).GetProperty("events").EnumerateArray().ToList();

    [Fact]
    public async Task A_review_shows_its_plan_waits_for_approval_then_runs_every_step_and_writes_a_report()
    {
        var (client, _) = await host.NewUserAsync();

        // 1. Submitted: nothing runs; the approval carries the plan.
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "review", path = "app", sca = false }));
        var jobId = submitted.GetProperty("job").GetProperty("id").GetString()!;
        var approval = submitted.GetProperty("approval");
        Assert.Equal("AwaitingApproval", submitted.GetProperty("job").GetProperty("state").GetString());
        Assert.Equal("run-plan", approval.GetProperty("kind").GetString());
        Assert.Equal("Run this review of \"app\" (4 steps)", approval.GetProperty("title").GetString());
        var detail = approval.GetProperty("detail").GetString()!;
        Assert.Contains("1. Scan \"app\"", detail);
        Assert.Contains("4. Write the report", detail);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/scans")).GetArrayLength());

        // 2. Approved: it runs to the end.
        await client.PostAsJsonAsync($"/approvals/{approval.GetProperty("id").GetString()}/approve", new { });
        var job = await WaitForEndAsync(client, jobId);
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());

        // 3. The stream: the plan, every step pending first, then each running and completed in order.
        var events = await EventsAsync(client, jobId);
        var planned = events.Single(e => e.GetProperty("type").GetString() == "plan").GetProperty("data").GetProperty("steps")
            .EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToArray();
        Assert.Equal(["scan", "assess:enterprise-sap", "assess:owasp-top10", "report"], planned);
        var steps = events.Where(e => e.GetProperty("type").GetString() == "step")
            .Select(e => $"{e.GetProperty("data").GetProperty("id").GetString()}={e.GetProperty("data").GetProperty("status").GetString()}")
            .ToArray();
        Assert.Equal(
        [
            "scan=pending", "assess:enterprise-sap=pending", "assess:owasp-top10=pending", "report=pending",
            "scan=running", "scan=completed",
            "assess:enterprise-sap=running", "assess:enterprise-sap=completed",
            "assess:owasp-top10=running", "assess:owasp-top10=completed",
            "report=running", "report=completed",
        ], steps);

        // 4. The review: one scan, two assessments, a report.
        var review = await client.GetFromJsonAsync<JsonElement>($"/reviews/{job.GetProperty("resultId").GetString()}");
        Assert.Equal(jobId, review.GetProperty("jobId").GetString());
        Assert.Equal(2, review.GetProperty("assessmentIds").GetArrayLength());
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/scans")).GetArrayLength());

        var report = await client.GetAsync($"/reviews/{review.GetProperty("id").GetString()}/report");
        Assert.Equal("text/markdown", report.Content.Headers.ContentType!.MediaType);
        var text = await report.Content.ReadAsStringAsync();
        Assert.Contains("# Security review of `app`", text);
        Assert.Contains("- **Dependencies:** not checked", text);
        Assert.Contains("main.py:2`", text);
        Assert.Contains("| OWASP Top 10", text);
        Assert.DoesNotContain("Model review by", text);

        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/reviews")).GetArrayLength());
        var actions = (await client.GetFromJsonAsync<JsonElement>("/audit?limit=50")).EnumerateArray()
            .Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Contains("review.complete", actions);
    }

    [Fact]
    public async Task With_plans_always_allowed_a_review_starts_at_once_and_a_local_model_reviews_the_findings()
    {
        var (client, _) = await host.NewUserAsync();
        await client.PutAsJsonAsync("/settings/permissions", new { allowedKinds = new[] { "run-plan" } });

        var submitted = await Json(await client.PostAsJsonAsync("/jobs",
            new { type = "review", path = "svc", sca = false, exclusions = "honour", catalogs = new[] { "OWASP-TOP10" }, model = "echo" }));

        Assert.Equal("Queued", submitted.GetProperty("state").GetString());
        var job = await WaitForEndAsync(client, submitted.GetProperty("id").GetString()!);
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());

        var steps = (await EventsAsync(client, job.GetProperty("id").GetString()!))
            .Where(e => e.GetProperty("type").GetString() == "step")
            .Select(e => e.GetProperty("data").GetProperty("id").GetString()!).Distinct().ToArray();
        Assert.Equal(["scan", "triage", "assess:owasp-top10", "report"], steps);   // the sub-runs' own steps are not shown

        var text = await client.GetStringAsync($"/reviews/{job.GetProperty("resultId").GetString()}/report");
        Assert.Contains("| real |", text);
        Assert.Contains("Model review by `echo` is advisory", text);

        var auto = (await client.GetFromJsonAsync<JsonElement>("/audit?limit=50")).EnumerateArray()
            .Single(e => e.GetProperty("action").GetString() == "approval.auto");
        Assert.Contains("run-plan", auto.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_review_with_a_cloud_model_asks_about_the_plan_and_the_code_together_and_denying_runs_nothing()
    {
        var (client, _) = await host.NewUserAsync();
        var sentBefore = host.Cloud.Requests.Count;

        var submitted = await Json(await client.PostAsJsonAsync("/jobs",
            new { type = "review", path = "app", sca = false, catalogs = new[] { "owasp-top10" }, model = "claude-haiku-4-5" }));

        var approval = submitted.GetProperty("approval");
        Assert.Equal("send-code-to-cloud-model", approval.GetProperty("kind").GetString());
        Assert.Equal(["run-plan", "send-code-to-cloud-model"],
            approval.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()!).ToArray());
        Assert.Contains("is sent to Anthropic", approval.GetProperty("detail").GetString());

        await client.PostAsync($"/approvals/{approval.GetProperty("id").GetString()}/deny", null);
        var job = await WaitForEndAsync(client, submitted.GetProperty("job").GetProperty("id").GetString()!);
        Assert.Equal("Cancelled", job.GetProperty("state").GetString());
        Assert.Equal(sentBefore, host.Cloud.Requests.Count);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/reviews")).GetArrayLength());
    }

    [Fact]
    public async Task Review_requests_are_validated_and_reviews_are_private()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "review", path = "app", catalogs = new[] { "nope" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "review", path = "app", model = "no-such-model" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "review", path = "../x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "review", path = "app", exclusions = "maybe" })).StatusCode);

        await client.PutAsJsonAsync("/settings/permissions", new { allowedKinds = new[] { "run-plan" } });
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "review", path = "app", sca = false, catalogs = new[] { "owasp-top10" } }));
        var job = await WaitForEndAsync(client, submitted.GetProperty("id").GetString()!);
        var reviewId = job.GetProperty("resultId").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/reviews/{reviewId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/reviews/{reviewId}/report")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/reviews")).StatusCode);
    }

    [Fact]
    public async Task A_review_can_dispatch_every_agent_and_its_report_brings_their_results_together()
    {
        var (client, _) = await host.NewUserAsync();
        await client.PutAsJsonAsync("/settings/permissions", new { allowedKinds = new[] { "run-plan" } });

        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new
        {
            type = "review", path = "svc", sca = false, exclusions = "honour", catalogs = new[] { "owasp-top10" },
            agentModel = "llama3.1", agents = new[] { "evidence", "threat-model", "sast" },
        }));
        var job = await WaitForEndAsync(client, submitted.GetProperty("id").GetString()!);
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());

        // The steps, in the order they run whatever order the agents were named in.
        var steps = (await EventsAsync(client, job.GetProperty("id").GetString()!))
            .Where(e => e.GetProperty("type").GetString() == "step")
            .Select(e => e.GetProperty("data").GetProperty("id").GetString()!).Distinct().ToArray();
        Assert.Equal(["scan", "sast", "threat-model", "assess:owasp-top10", "evidence:owasp-top10", "report"], steps);

        var review = await client.GetFromJsonAsync<JsonElement>($"/reviews/{job.GetProperty("resultId").GetString()}");
        var report = review.GetProperty("report").GetString()!;

        // Correlation: the scanner and the model point at the same line, and each table says so.
        Assert.Contains("- **Reported by the model:** 1 (1 at places the scanner also flagged, 0 the model's alone)", report);
        Assert.Contains("| `svc/loader.py:2` | — | yes |", report);
        Assert.Contains("| `svc/loader.py:2` | flagged it too |", report);

        // The threat model, in short, pointing at the full one.
        var threatModelRunId = review.GetProperty("threatModelRunId").GetString();
        Assert.Contains("## Threat model (`llama3.1`)", report);
        Assert.Contains("1 component(s), 1 threat(s): 1 medium.", report);
        Assert.Contains($"agent run `{threatModelRunId}`", report);
        Assert.Equal("threat-model", (await client.GetFromJsonAsync<JsonElement>($"/agent-runs/{threatModelRunId}")).GetProperty("agent").GetString());

        // The evidence agent's suggestions: counted in the report, stored on the assessment, changing nothing.
        Assert.Contains("- **OWASP Top 10 (2021):** 1 suggestion(s) by `llama3.1` — 1 \"Cannot tell\"", report);
        var assessment = await client.GetFromJsonAsync<JsonElement>($"/compliance/{review.GetProperty("assessmentIds")[0].GetString()}");
        Assert.Equal("Cannot tell", assessment.GetProperty("controls").EnumerateArray()
            .Single(c => c.GetProperty("controlId").GetString() == "A01").GetProperty("suggestion").GetProperty("suggestion").GetString());
        Assert.Equal(0, assessment.GetProperty("effective").GetProperty("operatorAnswered").GetInt32());
    }

    [Fact]
    public async Task Agents_need_a_model_and_must_be_ones_a_review_can_dispatch()
    {
        var (client, _) = await host.NewUserAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "review", path = "app", agents = new[] { "sast" } })).StatusCode);
        var unknown = await client.PostAsJsonAsync("/jobs", new { type = "review", path = "app", agentModel = "llama3.1", agents = new[] { "sast", "exploit" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("sast, threat-model, evidence", (await Json(unknown)).GetProperty("error").GetString());
    }
}
