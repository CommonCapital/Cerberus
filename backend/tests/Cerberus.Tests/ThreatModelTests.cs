using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api.Agents;

namespace Cerberus.Tests;

public class ThreatModelToolTests
{
    private static JsonElement J(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private sealed record Fixture(
        ThreatModelAgentRunner.RecordComponentTool Component, ThreatModelAgentRunner.RecordThreatTool Threat,
        List<ThreatComponent> Components, List<Threat> Threats, RecordingReporter Reporter);

    private static Fixture Build(TempCode code)
    {
        var components = new List<ThreatComponent>();
        var threats = new List<Threat>();
        var reporter = new RecordingReporter();
        return new Fixture(
            new ThreatModelAgentRunner.RecordComponentTool(code.Workspace(), components, reporter),
            new ThreatModelAgentRunner.RecordThreatTool(code.Workspace(), components, threats, reporter),
            components, threats, reporter);
    }

    private static object ThreatInput(string component = "Shell runner", string likelihood = "high", string impact = "high", string category = "Elevation of Privilege",
        object[]? evidence = null) => new
    {
        title = "Arbitrary command execution", category, component,
        description = "A caller controls cmd, which goes to the shell.", likelihood, impact,
        mitigation = "Use an argument list.", evidence = evidence ?? [new { file = "app.py", line = 2 }],
    };

    [Theory]
    [InlineData("low", "low", "low")]
    [InlineData("low", "high", "medium")]
    [InlineData("medium", "medium", "medium")]
    [InlineData("high", "medium", "high")]
    [InlineData("high", "high", "critical")]
    public void Risk_is_likelihood_times_impact(string likelihood, string impact, string risk)
    {
        Assert.Equal(risk, ThreatModelAgent.Risk(likelihood, impact));
    }

    [Fact]
    public async Task A_component_has_a_known_kind_a_unique_name_and_points_only_at_code_that_exists()
    {
        using var code = new TempCode();
        var f = Build(code);

        await Assert.ThrowsAsync<AgentToolException>(() => f.Component.RunAsync(J(new { name = "X", kind = "gadget", description = "d" }), default));
        await Assert.ThrowsAsync<AgentToolException>(() => f.Component.RunAsync(J(new { name = " ", kind = "process", description = "d" }), default));
        await Assert.ThrowsAsync<AgentToolException>(() => f.Component.RunAsync(J(new { name = "X", kind = "process", description = "d", where = new[] { new { file = "ghost.py", line = 1 } } }), default));
        await Assert.ThrowsAsync<AgentToolException>(() => f.Component.RunAsync(J(new { name = "X", kind = "process", description = "d", where = new[] { new { file = "../outside.txt", line = 1 } } }), default));

        var reply = await f.Component.RunAsync(J(new { name = "Shell runner", kind = "Process", description = "Runs commands.", where = new[] { new { file = "app.py", line = 2 } } }), default);
        await Assert.ThrowsAsync<AgentToolException>(() => f.Component.RunAsync(J(new { name = "shell RUNNER", kind = "process", description = "again" }), default));

        Assert.Equal("Recorded component 1: Shell runner.", reply);
        Assert.Equal(("Shell runner", "process", "Runs commands."), (f.Components[0].Name, f.Components[0].Kind, f.Components[0].Description));
        Assert.Equal(new CodeReference("app.py", 2), Assert.Single(f.Components[0].Where));
        Assert.Equal(["threat_component"], f.Reporter.Events.Select(e => e.Type).ToArray());
    }

    [Fact]
    public async Task A_threat_needs_a_recorded_component_a_stride_category_and_honest_levels()
    {
        using var code = new TempCode();
        var f = Build(code);

        var early = await Assert.ThrowsAsync<AgentToolException>(() => f.Threat.RunAsync(J(ThreatInput()), default));
        Assert.Contains("recordComponent first", early.Message);

        await f.Component.RunAsync(J(new { name = "Shell runner", kind = "process", description = "Runs commands." }), default);
        await Assert.ThrowsAsync<AgentToolException>(() => f.Threat.RunAsync(J(ThreatInput(component: "Database")), default));
        await Assert.ThrowsAsync<AgentToolException>(() => f.Threat.RunAsync(J(ThreatInput(category: "bad vibes")), default));
        await Assert.ThrowsAsync<AgentToolException>(() => f.Threat.RunAsync(J(ThreatInput(likelihood: "certain")), default));
        await Assert.ThrowsAsync<AgentToolException>(() => f.Threat.RunAsync(J(ThreatInput(evidence: [new { file = "app.py", line = 400 }])), default));
        Assert.Empty(f.Threats);

        var reply = await f.Threat.RunAsync(J(ThreatInput(component: "shell runner")), default);
        await Assert.ThrowsAsync<AgentToolException>(() => f.Threat.RunAsync(J(ThreatInput()), default));   // twice

        Assert.Equal("Recorded as T1 (risk: critical).", reply);
        var threat = Assert.Single(f.Threats);
        Assert.Equal(("T1", "elevation-of-privilege", "Shell runner", "critical"), (threat.Id, threat.Category, threat.Component, threat.Risk));
        Assert.Equal(new CodeReference("app.py", 2), Assert.Single(threat.Evidence));
        Assert.Equal("threat", f.Reporter.Events[^1].Type);
    }

    [Fact]
    public void The_report_lists_components_and_threats_most_severe_first_and_says_what_it_is()
    {
        List<ThreatComponent> components = [new("Upload | endpoint", "entry-point", "Takes files\nfrom users.", [new CodeReference("api.py", 10)])];
        List<Threat> threats =
        [
            new("T1", "Log flooding", "denial-of-service", "Upload | endpoint", "Large uploads fill the disk.", "low", "medium", "low", null, []),
            new("T2", "Path traversal", "tampering", "Upload | endpoint", "A file name with ../ escapes the folder.", "high", "high", "critical", "Normalize names.", [new CodeReference("api.py", 14)]),
        ];

        var partial = ThreatModelReport.Write("svc", "llama3.1", components, threats, "A small upload service.", complete: false, new DateTime(2026, 11, 12, 9, 30, 0, DateTimeKind.Utc));
        var empty = ThreatModelReport.Write("svc", "llama3.1", [], [], "", complete: true, DateTime.UtcNow);

        Assert.Contains("# Threat model of `svc`", partial);
        Assert.Contains("Generated 2026-11-12 09:30 UTC by Cerberus with `llama3.1`.", partial);
        Assert.Contains("not a finding of fact", partial);
        Assert.Contains("**The run did not complete; this model is partial.**", partial);
        Assert.Contains("| Upload \\| endpoint | entry-point | Takes files from users. | `api.py:10` |", partial);
        Assert.Contains("2 threat(s): 1 critical, 1 low.", partial);
        Assert.True(partial.IndexOf("| T2 | critical |", StringComparison.Ordinal) < partial.IndexOf("| T1 | low |", StringComparison.Ordinal));
        Assert.Contains("- **Mitigation:** Normalize names.", partial);
        Assert.Contains("- **In the code:** `api.py:14`", partial);

        Assert.DoesNotContain("did not complete", empty);
        Assert.DoesNotContain("## Summary", empty);
        Assert.Equal(2, empty.Split("None recorded.").Length - 1);
    }
}

[Collection("api")]
public class ThreatModelApiTests(ApiHost host)
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

    [Fact]
    public async Task The_agent_records_components_and_threats_which_are_streamed_stored_and_written_up()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();

        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "threat-model", path = "svc", model = "llama3.1" }));
        Assert.Equal("Queued", submitted.GetProperty("state").GetString());   // a local model: no approval
        var job = await WaitForEndAsync(client, submitted.GetProperty("id").GetString()!);
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());

        var events = (await client.GetFromJsonAsync<JsonElement>($"/jobs/{job.GetProperty("id").GetString()}/events?after=0"))
            .GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(["listDirectory", "recordComponent", "recordThreat"],
            events.Where(e => e.GetProperty("type").GetString() == "tool_call").Select(e => e.GetProperty("data").GetProperty("name").GetString()!).ToArray());
        Assert.Equal("process", events.Single(e => e.GetProperty("type").GetString() == "threat_component").GetProperty("data").GetProperty("kind").GetString());
        var streamed = events.Single(e => e.GetProperty("type").GetString() == "threat").GetProperty("data");
        Assert.Equal(("T1", "tampering", "medium"), (streamed.GetProperty("id").GetString(), streamed.GetProperty("category").GetString(), streamed.GetProperty("risk").GetString()));

        var runId = job.GetProperty("resultId").GetString();
        var run = await client.GetFromJsonAsync<JsonElement>($"/agent-runs/{runId}");
        Assert.Equal(("threat-model", true, 1, true), (run.GetProperty("agent").GetString(), run.GetProperty("complete").GetBoolean(),
            run.GetProperty("findingCount").GetInt32(), run.GetProperty("hasReport").GetBoolean()));
        Assert.Equal(1, run.GetProperty("findings").GetProperty("components").GetArrayLength());
        Assert.Equal("The code under review", run.GetProperty("findings").GetProperty("threats")[0].GetProperty("component").GetString());

        var report = await client.GetAsync($"/agent-runs/{runId}/report");
        Assert.Equal("text/markdown", report.Content.Headers.ContentType!.MediaType);
        var text = await report.Content.ReadAsStringAsync();
        Assert.Contains("# Threat model of `svc`", text);
        Assert.Contains("| T1 | medium | Untrusted input reaches the code unchecked (placeholder) | tampering | The code under review |", text);
        Assert.Contains("Recorded one component and one placeholder threat.", text);

        // Not a scan, private, and audited with where the code went.
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/scans")).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/agent-runs/{runId}/report")).StatusCode);
        var audit = (await client.GetFromJsonAsync<JsonElement>("/audit?limit=50")).EnumerateArray()
            .Single(e => e.GetProperty("action").GetString() == "agent.run");
        Assert.Contains("Threat-model agent run", audit.GetProperty("detail").GetString());
        Assert.Contains("1 component(s), 1 threat(s)", audit.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Requests_are_validated_a_cloud_model_waits_for_approval_and_other_agents_have_no_report()
    {
        var (client, _) = await host.NewUserAsync();
        async Task<HttpStatusCode> Submit(object body) => (await client.PostAsJsonAsync("/jobs", body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Submit(new { type = "threat-model", path = "app" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Submit(new { type = "threat-model", path = "app", model = "llama3.1", maxSteps = 51 }));
        Assert.Equal(HttpStatusCode.BadRequest, await Submit(new { type = "threat-model", path = "app", model = "llama3.1", instructions = new string('x', 2001) }));
        Assert.Equal(HttpStatusCode.BadRequest, await Submit(new { type = "threat-model", path = "../x", model = "llama3.1" }));

        var sentBefore = host.Cloud.Requests.Count;
        var cloud = await Json(await client.PostAsJsonAsync("/jobs", new { type = "threat-model", path = "app", model = "claude-haiku-4-5", maxSteps = 7 }));
        var approval = cloud.GetProperty("approval");
        Assert.Equal("send-code-to-cloud-model", approval.GetProperty("kind").GetString());
        Assert.Equal("Let claude-haiku-4-5 (Anthropic) read the code under \"app\" for a threat model", approval.GetProperty("title").GetString());
        Assert.Contains("up to 7 steps", approval.GetProperty("detail").GetString());
        await client.PostAsync($"/approvals/{approval.GetProperty("id").GetString()}/deny", null);
        Assert.Equal(sentBefore, host.Cloud.Requests.Count);

        // The SAST agent writes no report.
        var sast = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "other", model = "llama3.1" }));
        var done = await WaitForEndAsync(client, sast.GetProperty("id").GetString()!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/agent-runs/{done.GetProperty("resultId").GetString()}/report")).StatusCode);
    }
}
