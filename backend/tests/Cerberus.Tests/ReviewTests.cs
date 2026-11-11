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
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "review", path = "app", model = "gpt-nonexistent" })).StatusCode);
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
}
