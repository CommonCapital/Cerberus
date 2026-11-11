using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api.Agents;
using Cerberus.Compliance;
using Cerberus.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Cerberus.Tests;

public class SuggestVerdictToolTests
{
    private static JsonElement J(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private static (EvidenceAgentRunner.SuggestVerdictTool Tool, List<ControlSuggestion> Saved) Build(TempCode code)
    {
        var saved = new List<ControlSuggestion>();
        var tool = new EvidenceAgentRunner.SuggestVerdictTool(
            code.Workspace(), ["A01", "A04"], OperatorResponses.DefaultScale, "llama3.1", "job-1",
            s => { saved.Add(s); return Task.CompletedTask; });
        return (tool, saved);
    }

    [Fact]
    public async Task A_definite_answer_needs_evidence_that_exists_in_the_code()
    {
        using var code = new TempCode();
        var (tool, saved) = Build(code);
        var evidence = new[] { new { file = "app.py", line = 2 } };

        await Assert.ThrowsAsync<AgentToolException>(() => tool.RunAsync(J(new { controlId = "A01", suggestion = "Compliant", reasoning = "Looks fine." }), default));
        await Assert.ThrowsAsync<AgentToolException>(() => tool.RunAsync(J(new { controlId = "A01", suggestion = "Non-compliant", reasoning = "x", evidence = new[] { new { file = "ghost.py", line = 1 } } }), default));
        await Assert.ThrowsAsync<AgentToolException>(() => tool.RunAsync(J(new { controlId = "A01", suggestion = "Non-compliant", reasoning = "x", evidence = new[] { new { file = "app.py", line = 99 } } }), default));
        await Assert.ThrowsAsync<AgentToolException>(() => tool.RunAsync(J(new { controlId = "A01", suggestion = "Non-compliant", reasoning = "x", evidence = new[] { new { file = "../outside.txt", line = 1 } } }), default));
        Assert.Empty(saved);

        var reply = await tool.RunAsync(J(new { controlId = "a01", suggestion = "non compliant", reasoning = "Commands are built from input.", evidence }), default);

        Assert.Equal("Recorded. Still without a suggestion: A04.", reply);
        var suggestion = Assert.Single(saved);
        Assert.Equal(("A01", "Non-compliant", "llama3.1", "job-1"), (suggestion.ControlId, suggestion.Suggestion, suggestion.Model, suggestion.JobId));
        Assert.Equal(new EvidenceReference("app.py", 2, "os.system(cmd)"), Assert.Single(suggestion.Evidence));
    }

    [Fact]
    public async Task Cannot_tell_and_not_applicable_need_no_evidence_but_always_a_reason()
    {
        using var code = new TempCode();
        var (tool, saved) = Build(code);

        await Assert.ThrowsAsync<AgentToolException>(() => tool.RunAsync(J(new { controlId = "A01", suggestion = "Cannot tell", reasoning = " " }), default));
        await tool.RunAsync(J(new { controlId = "A01", suggestion = "Cannot tell", reasoning = "This is about a policy document." }), default);
        var done = await tool.RunAsync(J(new { controlId = "A04", suggestion = "Not applicable", reasoning = "No design artefacts apply to a script." }), default);

        Assert.Equal("Recorded. Every control has a suggestion now.", done);
        Assert.Equal(["Cannot tell", "Not applicable"], saved.Select(s => s.Suggestion).ToArray());
        Assert.Equal(2, tool.Count);
    }

    [Fact]
    public async Task Only_the_controls_it_was_given_and_only_answers_on_the_scale()
    {
        using var code = new TempCode();
        var (tool, saved) = Build(code);

        var control = await Assert.ThrowsAsync<AgentToolException>(() => tool.RunAsync(J(new { controlId = "A02", suggestion = "Cannot tell", reasoning = "x" }), default));
        var answer = await Assert.ThrowsAsync<AgentToolException>(() => tool.RunAsync(J(new { controlId = "A01", suggestion = "Probably fine", reasoning = "x" }), default));

        Assert.Contains("A01, A04", control.Message);
        Assert.Contains("Cannot tell", answer.Message);
        Assert.Empty(saved);
    }

    [Fact]
    public void A_run_looks_at_unanswered_controls_the_engine_could_not_judge_or_at_the_ones_named()
    {
        ControlResult Control(string id, ControlVerdict verdict) => new(id, "S", id, verdict, verdict != ControlVerdict.NotAssessed, "", []);
        List<ControlResult> matrix = [Control("A01", ControlVerdict.NotAssessed), Control("A02", ControlVerdict.NonCompliant), Control("A04", ControlVerdict.NotAssessed), Control("A07", ControlVerdict.NotAssessed)];
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a04" };

        var byDefault = EvidenceAgent.Select(matrix, answered, new EvidenceJobParameters("x", "m"));
        var limited = EvidenceAgent.Select(matrix, answered, new EvidenceJobParameters("x", "m", MaxControls: 1));
        var named = EvidenceAgent.Select(matrix, answered, new EvidenceJobParameters("x", "m", ControlIds: ["a02", "A04"]));

        Assert.Equal(["A01", "A07"], byDefault.Select(c => c.ControlId).ToArray());
        Assert.Equal(["A01"], limited.Select(c => c.ControlId).ToArray());
        Assert.Equal(["A02", "A04"], named.Select(c => c.ControlId).ToArray());
    }
}

[Collection("api")]
public class EvidenceApiTests(ApiHost host)
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

    private static async Task<string> AssessAsync(HttpClient client, string path = "svc") =>
        (await Json(await client.PostAsJsonAsync("/compliance/assess", new { catalogId = "owasp-top10", path, sca = false }))).GetProperty("id").GetString()!;

    private static JsonElement Control(JsonElement assessment, string id) =>
        assessment.GetProperty("controls").EnumerateArray().Single(c => c.GetProperty("controlId").GetString() == id);

    [Fact]
    public async Task The_agent_suggests_an_answer_which_is_stored_beside_the_assessment_and_changes_nothing()
    {
        var (client, _) = await host.NewUserAsync();
        var assessmentId = await AssessAsync(client);
        var before = await client.GetFromJsonAsync<JsonElement>($"/compliance/{assessmentId}");

        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "evidence", assessmentId, model = "llama3.1" }));
        Assert.Equal(("Queued", "svc"), (submitted.GetProperty("state").GetString(), submitted.GetProperty("targetPath").GetString()));
        var job = await WaitForEndAsync(client, submitted.GetProperty("id").GetString()!);
        Assert.Equal(("Succeeded", assessmentId), (job.GetProperty("state").GetString(), job.GetProperty("resultId").GetString()));

        // The first control the engine could not judge has a suggestion; the others have none.
        var after = await client.GetFromJsonAsync<JsonElement>($"/compliance/{assessmentId}");
        var suggestion = Control(after, "A01").GetProperty("suggestion");
        Assert.Equal(("Cannot tell", "llama3.1", job.GetProperty("id").GetString()),
            (suggestion.GetProperty("suggestion").GetString(), suggestion.GetProperty("model").GetString(), suggestion.GetProperty("jobId").GetString()));
        Assert.Equal(JsonValueKind.Null, Control(after, "A04").GetProperty("suggestion").ValueKind);

        // Nothing about the assessment itself moved.
        Assert.Equal(before.GetProperty("summary").GetRawText(), after.GetProperty("summary").GetRawText());
        Assert.Equal(before.GetProperty("effective").GetRawText(), after.GetProperty("effective").GetRawText());
        Assert.Equal(JsonValueKind.Null, Control(after, "A01").GetProperty("response").ValueKind);

        // On the stream: the tool calls and the suggestion.
        var events = (await client.GetFromJsonAsync<JsonElement>($"/jobs/{job.GetProperty("id").GetString()}/events?after=0"))
            .GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(["listDirectory", "suggestVerdict"],
            events.Where(e => e.GetProperty("type").GetString() == "tool_call").Select(e => e.GetProperty("data").GetProperty("name").GetString()!).ToArray());
        Assert.Equal("A01", events.Single(e => e.GetProperty("type").GetString() == "suggestion").GetProperty("data").GetProperty("controlId").GetString());
        Assert.Contains((await client.GetFromJsonAsync<JsonElement>("/audit?limit=50")).EnumerateArray(),
            e => e.GetProperty("action").GetString() == "evidence.run" && e.GetProperty("detail").GetString()!.Contains("advisory"));

        // "Cannot tell" is not an answer to accept; it can be dismissed.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/compliance/{assessmentId}/suggestions/A01/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/compliance/{assessmentId}/suggestions/A01")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/compliance/{assessmentId}/suggestions/A01")).StatusCode);
    }

    [Fact]
    public async Task Accepting_a_suggestion_makes_it_the_operators_answer_under_the_same_rules_as_any_answer()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();
        var assessmentId = await AssessAsync(client);

        // A definite suggestion, as a real model would leave it.
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
            var assessment = db.ComplianceAssessments.Single(a => a.Id == assessmentId);
            EvidenceAgent.WriteSuggestions(assessment,
            [
                new ControlSuggestion("A01", "Non-compliant", "The loader acts on any caller's data without an authorization check.",
                    [new EvidenceReference("loader.py", 2, "obj = pickle.loads(blob)")], "llama3.1", null, DateTime.UtcNow),
            ]);
            db.SaveChanges();
        }
        var before = (await client.GetFromJsonAsync<JsonElement>($"/compliance/{assessmentId}")).GetProperty("effective");

        // Someone else cannot; and the response schema still applies (a plan is required when not compliant).
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/compliance/{assessmentId}/suggestions/A01/accept", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/compliance/{assessmentId}/suggestions/A04/accept", null)).StatusCode);
        var withoutPlan = await client.PostAsync($"/compliance/{assessmentId}/suggestions/A01/accept", null);
        Assert.Equal(HttpStatusCode.BadRequest, withoutPlan.StatusCode);
        Assert.Contains("action plan", (await Json(withoutPlan)).GetProperty("error").GetString());

        var accepted = await client.PostAsJsonAsync($"/compliance/{assessmentId}/suggestions/a01/accept", new { actionPlan = "Add an authorization check.", timeLimitDays = 30 });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>($"/compliance/{assessmentId}");
        var response = Control(after, "A01").GetProperty("response");
        Assert.Equal("Non-compliant", response.GetProperty("verdict").GetString());
        Assert.StartsWith("Accepted from a suggestion by llama3.1: The loader acts", response.GetProperty("justification").GetString());
        Assert.Contains("loader.py:2", response.GetProperty("justification").GetString());
        Assert.Equal(before.GetProperty("nonCompliant").GetInt32() + 1, after.GetProperty("effective").GetProperty("nonCompliant").GetInt32());
        Assert.Contains((await client.GetFromJsonAsync<JsonElement>("/audit?limit=50")).EnumerateArray(),
            e => e.GetProperty("action").GetString() == "evidence.accept");

        // An answered control is no longer one the agent looks at by default.
        var again = await Json(await client.PostAsJsonAsync("/jobs", new { type = "evidence", assessmentId, model = "llama3.1" }));
        await WaitForEndAsync(client, again.GetProperty("id").GetString()!);
        var final = await client.GetFromJsonAsync<JsonElement>($"/compliance/{assessmentId}");
        Assert.Equal("Cannot tell", Control(final, "A04").GetProperty("suggestion").GetProperty("suggestion").GetString());
    }

    [Fact]
    public async Task Requests_are_validated_and_a_cloud_model_waits_for_approval()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();
        var assessmentId = await AssessAsync(client, "app");
        async Task<HttpStatusCode> Submit(object body) => (await client.PostAsJsonAsync("/jobs", body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Submit(new { type = "evidence", model = "llama3.1" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Submit(new { type = "evidence", assessmentId }));
        Assert.Equal(HttpStatusCode.BadRequest, await Submit(new { type = "evidence", assessmentId, model = "llama3.1", maxControls = 21 }));
        Assert.Equal(HttpStatusCode.BadRequest, await Submit(new { type = "evidence", assessmentId, model = "llama3.1", maxSteps = 0 }));
        Assert.Equal(HttpStatusCode.BadRequest, await Submit(new { type = "evidence", assessmentId, model = "llama3.1", controlIds = new[] { "Z99" } }));
        Assert.Equal(HttpStatusCode.NotFound, await Submit(new { type = "evidence", assessmentId = "no-such", model = "llama3.1" }));
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/jobs", new { type = "evidence", assessmentId, model = "llama3.1" })).StatusCode);

        var sentBefore = host.Cloud.Requests.Count;
        var cloud = await Json(await client.PostAsJsonAsync("/jobs", new { type = "evidence", assessmentId, model = "claude-haiku-4-5", controlIds = new[] { "A01", "A04" } }));
        var approval = cloud.GetProperty("approval");
        Assert.Equal("send-code-to-cloud-model", approval.GetProperty("kind").GetString());
        Assert.Contains("evidence for 2 control(s)", approval.GetProperty("detail").GetString());
        await client.PostAsync($"/approvals/{approval.GetProperty("id").GetString()}/deny", null);
        Assert.Equal(sentBefore, host.Cloud.Requests.Count);
    }
}
