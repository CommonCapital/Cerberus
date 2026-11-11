using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Jobs;

namespace Cerberus.Tests;

public class OperatorQuestionValidationTests
{
    private static readonly OperatorQuestionSpec Choice = new("Pick one", [new("Honour them"), new("Scan everything")]);
    private static readonly OperatorQuestionSpec Multi = Choice with { MultiSelect = true };
    private static readonly OperatorQuestionSpec Open = new("Why?", [], AllowText: true);
    private static readonly OperatorQuestionSpec ChoiceOrText = Choice with { AllowText = true };

    [Fact]
    public void A_single_choice_is_returned_in_the_questions_own_wording()
    {
        var (answer, error) = OperatorQuestions.ValidateAnswer(Choice, ["  scan EVERYTHING "], null);

        Assert.Null(error);
        Assert.Equal(["Scan everything"], answer!.Selected);
        Assert.Null(answer.Text);
    }

    [Fact]
    public void Several_choices_need_a_multi_select_question_and_keep_the_questions_order()
    {
        Assert.Equal("Select exactly one option.", OperatorQuestions.ValidateAnswer(Choice, ["Scan everything", "Honour them"], null).Error);

        var (answer, _) = OperatorQuestions.ValidateAnswer(Multi, ["Scan everything", "Honour them", "honour them"], null);
        Assert.Equal(["Honour them", "Scan everything"], answer!.Selected);
    }

    [Fact]
    public void Unknown_options_are_refused_and_the_valid_ones_named()
    {
        var error = OperatorQuestions.ValidateAnswer(Choice, ["Maybe"], null).Error;

        Assert.Contains("\"Maybe\" is not one of the options", error);
        Assert.Contains("Honour them, Scan everything", error);
    }

    [Fact]
    public void Free_text_is_only_accepted_where_the_question_allows_it()
    {
        Assert.Contains("does not take a free-text answer", OperatorQuestions.ValidateAnswer(Choice, null, "because").Error);
        Assert.Equal("because", OperatorQuestions.ValidateAnswer(Open, null, "  because ").Answer!.Text);

        var both = OperatorQuestions.ValidateAnswer(ChoiceOrText, ["Honour them"], "it is our own repo").Answer!;
        Assert.Equal(["Honour them"], both.Selected);
        Assert.Equal("it is our own repo", both.Text);
    }

    [Fact]
    public void An_empty_reply_is_not_an_answer()
    {
        Assert.Equal("Select an option.", OperatorQuestions.ValidateAnswer(Choice, [], " ").Error);
        Assert.Equal("Select an option or give an answer text.", OperatorQuestions.ValidateAnswer(ChoiceOrText, null, null).Error);
        Assert.Equal("An answer text is required.", OperatorQuestions.ValidateAnswer(Open, [" "], null).Error);
    }

    [Fact]
    public void Overlong_text_is_refused()
    {
        Assert.Contains("limited to", OperatorQuestions.ValidateAnswer(Open, null, new string('x', 2001)).Error);
    }

    [Fact]
    public void A_question_must_be_answerable()
    {
        Assert.Null(OperatorQuestions.ValidateSpec(Choice));
        Assert.Null(OperatorQuestions.ValidateSpec(Open));
        Assert.NotNull(OperatorQuestions.ValidateSpec(new("  ", [new("a")])));
        Assert.NotNull(OperatorQuestions.ValidateSpec(new("No way to answer", [])));
        Assert.NotNull(OperatorQuestions.ValidateSpec(new("Twins", [new("Yes"), new("yes")])));
        Assert.NotNull(OperatorQuestions.ValidateSpec(new("Blank", [new(" ")])));
        Assert.NotNull(OperatorQuestions.ValidateSpec(new("Too many", Enumerable.Range(0, 9).Select(i => new QuestionOption($"o{i}")).ToList())));
    }
}

public class JobTimeLimitTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(200);

    private static Task<JobOutcome> Run(Func<CancellationToken, JobTimeLimit, Task<string?>> work, TimeSpan timeout) =>
        JobExecutor.RunAsync("job-1", work, new JobCancellationRegistry(), timeout, Grace, CancellationToken.None);

    [Fact]
    public async Task Time_spent_waiting_for_a_person_does_not_count()
    {
        var outcome = await Run(async (ct, limit) =>
        {
            using (limit.Suspend())
            {
                await Task.Delay(600, ct);    // four times the limit, but the clock is stopped
            }
            return "done";
        }, TimeSpan.FromMilliseconds(150));

        Assert.Equal(new JobOutcome(JobState.Succeeded, "done", null), outcome);
    }

    [Fact]
    public async Task The_limit_resumes_with_the_time_it_had_left()
    {
        var outcome = await Run(async (ct, limit) =>
        {
            using (limit.Suspend())
            {
                await Task.Delay(300, ct);
            }
            await Task.Delay(Timeout.Infinite, ct);   // back on the clock: must now time out
            return null;
        }, TimeSpan.FromMilliseconds(150));

        Assert.Equal(JobState.TimedOut, outcome.State);
    }

    [Fact]
    public async Task Nested_pauses_resume_only_when_the_last_one_ends()
    {
        var outcome = await Run(async (ct, limit) =>
        {
            var outer = limit.Suspend();
            var inner = limit.Suspend();
            inner.Dispose();
            inner.Dispose();                  // disposing twice must not resume early
            await Task.Delay(400, ct);        // still paused by the outer one
            outer.Dispose();
            return "done";
        }, TimeSpan.FromMilliseconds(150));

        Assert.Equal(JobState.Succeeded, outcome.State);
    }

    [Fact]
    public async Task A_paused_job_can_still_be_cancelled()
    {
        var registry = new JobCancellationRegistry();
        var paused = new TaskCompletionSource();
        var run = JobExecutor.RunAsync("job-1", async (ct, limit) =>
        {
            using (limit.Suspend())
            {
                paused.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return null;
        }, registry, TimeSpan.FromMilliseconds(150), Grace, CancellationToken.None);
        await paused.Task;
        await Task.Delay(300);

        registry.Cancel("job-1");

        Assert.Equal(JobState.Cancelled, (await run).State);
    }
}

[Collection("api")]
public class OperatorQuestionIntegrationTests(ApiHost host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> WaitForStateAsync(HttpClient client, string jobId, params string[] states)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}");
            if (states.Contains(job.GetProperty("state").GetString())) return job;
            Assert.True(DateTime.UtcNow < deadline, $"job is {job.GetProperty("state").GetString()}, expected one of {string.Join("/", states)}");
            await Task.Delay(40);
        }
    }

    private static Task<JsonElement> WaitForEndAsync(HttpClient client, string jobId) =>
        WaitForStateAsync(client, jobId, "Succeeded", "Failed", "Cancelled", "TimedOut");

    /// <summary>Submits a scan of the fixture whose target suppresses one finding, and waits for the question.</summary>
    private static async Task<(string JobId, JsonElement Question)> AskedAsync(HttpClient client)
    {
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "svc", sca = false }));
        var jobId = submitted.GetProperty("id").GetString()!;
        await WaitForStateAsync(client, jobId, "AwaitingInput");

        var pending = await client.GetFromJsonAsync<JsonElement>($"/questions?status=pending&jobId={jobId}");
        return (jobId, pending.EnumerateArray().Single());
    }

    private static async Task<List<string>> LabelsAsync(HttpClient client, string jobId)
    {
        var page = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}/events");
        return page.GetProperty("events").EnumerateArray().Select(e =>
        {
            var data = e.GetProperty("data");
            return e.GetProperty("type").GetString() switch
            {
                "state" => $"state:{data.GetProperty("state").GetString()}",
                "step" => $"step:{data.GetProperty("id").GetString()}:{data.GetProperty("status").GetString()}",
                var type => type!,
            };
        }).ToList();
    }

    [Fact]
    public async Task A_scan_job_asks_before_trusting_the_targets_own_exclusions_and_acts_on_the_answer()
    {
        var (client, _) = await host.NewUserAsync();
        var (jobId, question) = await AskedAsync(client);
        var questionId = question.GetProperty("id").GetString();

        // The question is specific and offers the two real choices.
        Assert.Contains("excludes 0 file(s)", question.GetProperty("text").GetString());
        Assert.Contains("marks 1 finding(s) with cerberus:ignore", question.GetProperty("text").GetString());
        Assert.Equal(["Honour them", "Scan everything"],
            question.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetString()!).ToArray());
        Assert.Equal(jobId, question.GetProperty("jobId").GetString());

        // Nothing has been stored yet: the job is genuinely waiting.
        await Task.Delay(250);
        Assert.Equal("AwaitingInput", (await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}")).GetProperty("state").GetString());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/scans")).GetArrayLength());

        var answered = await client.PostAsJsonAsync($"/questions/{questionId}/answer", new { selected = new[] { "Scan everything" } });
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Equal("Answered", (await Json(answered)).GetProperty("status").GetString());

        var job = await WaitForEndAsync(client, jobId);
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());

        // The operator chose not to trust the marker, so the suppressed finding now counts.
        var scan = await client.GetFromJsonAsync<JsonElement>($"/scans/{job.GetProperty("resultId").GetString()}");
        Assert.Equal(2, scan.GetProperty("findingCount").GetInt32());
        Assert.All(scan.GetProperty("findings").EnumerateArray(), f => Assert.False(f.GetProperty("suppressed").GetBoolean()));

        // The whole exchange is on the run's stream, in order, and in the audit log.
        Assert.Equal(
        [
            "state:Queued", "state:Running", "step:static:running",
            "state:AwaitingInput", "question", "question_resolved", "state:Running",
            "finding", "finding", "progress", "step:static:completed",
            "step:dependencies:skipped", "step:store:running", "step:store:completed",
            "state:Succeeded",
        ], await LabelsAsync(client, jobId));

        var audit = await client.GetFromJsonAsync<JsonElement>("/audit");
        var entries = audit.EnumerateArray().ToDictionary(e => e.GetProperty("action").GetString()!, e => e.GetProperty("detail").GetString()!);
        Assert.Contains("Honour the target's own exclusions?", entries["question.ask"]);
        Assert.EndsWith(": Scan everything", entries["question.answer"]);
        Assert.Contains("the target's own exclusions were disregarded", entries["scan.run"]);
    }

    [Fact]
    public async Task Choosing_to_honour_them_keeps_the_suppressed_finding_out_of_the_count()
    {
        var (client, _) = await host.NewUserAsync();
        var (jobId, question) = await AskedAsync(client);

        await client.PostAsJsonAsync($"/questions/{question.GetProperty("id").GetString()}/answer", new { selected = new[] { "honour them" } });
        var job = await WaitForEndAsync(client, jobId);

        var scan = await client.GetFromJsonAsync<JsonElement>($"/scans/{job.GetProperty("resultId").GetString()}");
        Assert.Equal(1, scan.GetProperty("findingCount").GetInt32());
        Assert.Equal(2, scan.GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public async Task Invalid_answers_are_refused_and_the_job_keeps_waiting()
    {
        var (client, _) = await host.NewUserAsync();
        var (jobId, question) = await AskedAsync(client);
        var url = $"/questions/{question.GetProperty("id").GetString()}/answer";

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new { selected = new[] { "Whatever" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new { selected = new[] { "Honour them", "Scan everything" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new { text = "do what you think is best" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(url, new { })).StatusCode);
        Assert.Equal("AwaitingInput", (await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}")).GetProperty("state").GetString());

        // One valid answer; after that the question is closed.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(url, new { selected = new[] { "Honour them" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(url, new { selected = new[] { "Scan everything" } })).StatusCode);
        await WaitForEndAsync(client, jobId);
    }

    [Fact]
    public async Task Cancelling_a_job_that_is_waiting_withdraws_its_question()
    {
        var (client, _) = await host.NewUserAsync();
        var (jobId, question) = await AskedAsync(client);
        var questionId = question.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/jobs/{jobId}/cancel", null)).StatusCode);
        var job = await WaitForEndAsync(client, jobId);

        Assert.Equal("Cancelled", job.GetProperty("state").GetString());
        Assert.Equal("Cancelled", (await client.GetFromJsonAsync<JsonElement>($"/questions/{questionId}")).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.PostAsJsonAsync($"/questions/{questionId}/answer", new { selected = new[] { "Honour them" } })).StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/scans")).GetArrayLength());

        var labels = await LabelsAsync(client, jobId);
        Assert.Equal(["question", "question_resolved", "state:Cancelled"], labels.Skip(labels.IndexOf("question")).ToArray());
    }

    [Fact]
    public async Task Aborting_the_engagement_stops_a_job_that_is_waiting_for_an_answer()
    {
        var (client, _) = await host.NewUserAsync();
        var id = (await Json(await client.PostAsJsonAsync("/engagements", new { name = "Abort while asking" }))).GetProperty("id").GetString();
        await client.PostAsJsonAsync($"/engagements/{id}/targets", new { locator = "svc", ownershipAttested = true });
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "svc", sca = false, engagementId = id }));
        var jobId = submitted.GetProperty("id").GetString()!;
        await WaitForStateAsync(client, jobId, "AwaitingInput");

        var aborted = await Json(await client.PostAsync($"/engagements/{id}/abort", null));
        Assert.Equal(1, aborted.GetProperty("jobsCancelled").GetProperty("running").GetInt32());

        Assert.Equal("Cancelled", (await WaitForEndAsync(client, jobId)).GetProperty("state").GetString());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/questions?status=pending")).GetArrayLength());
    }

    [Theory]
    [InlineData("honour", 1)]
    [InlineData("ignore", 2)]
    public async Task Deciding_up_front_means_no_question(string exclusions, int expectedCount)
    {
        var (client, _) = await host.NewUserAsync();

        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "svc", sca = false, exclusions }));
        var job = await WaitForEndAsync(client, submitted.GetProperty("id").GetString()!);
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/questions")).GetArrayLength());
        var scan = await client.GetFromJsonAsync<JsonElement>($"/scans/{job.GetProperty("resultId").GetString()}");
        Assert.Equal(expectedCount, scan.GetProperty("findingCount").GetInt32());

        // The synchronous endpoint takes the same choice, but cannot ask.
        var sync = await Json(await client.PostAsJsonAsync("/scan", new { path = "svc", sca = false, exclusions }));
        Assert.Equal(expectedCount, sync.GetProperty("findingCount").GetInt32());
    }

    [Fact]
    public async Task Unknown_exclusion_choices_are_refused()
    {
        var (client, _) = await host.NewUserAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "svc", exclusions = "sometimes" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/scan", new { path = "svc", sca = false, exclusions = "ask" })).StatusCode);
    }

    [Fact]
    public async Task A_target_without_exclusions_is_not_asked_about()
    {
        var (client, _) = await host.NewUserAsync();

        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false }));
        await WaitForEndAsync(client, submitted.GetProperty("id").GetString()!);

        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/questions")).GetArrayLength());
    }

    [Fact]
    public async Task Questions_are_private_to_their_owner()
    {
        var (alice, _) = await host.NewUserAsync();
        var (bob, _) = await host.NewUserAsync();
        var (jobId, question) = await AskedAsync(alice);
        var questionId = question.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/questions/{questionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await bob.PostAsJsonAsync($"/questions/{questionId}/answer", new { selected = new[] { "Scan everything" } })).StatusCode);
        Assert.Equal(0, (await bob.GetFromJsonAsync<JsonElement>("/questions")).GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/questions")).StatusCode);

        await alice.PostAsJsonAsync($"/questions/{questionId}/answer", new { selected = new[] { "Honour them" } });
        await WaitForEndAsync(alice, jobId);
    }

    [Fact]
    public async Task An_unanswered_question_expires_and_the_job_falls_back_to_the_default()
    {
        using var impatient = host.StartVariant("CERBERUS_QUESTION_TIMEOUT_SECONDS", "0.4");
        var client = await ApiHost.AuthenticatedClientAsync(impatient);

        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "svc", sca = false }));
        var jobId = submitted.GetProperty("id").GetString()!;
        var job = await WaitForEndAsync(client, jobId);

        Assert.Equal("Succeeded", job.GetProperty("state").GetString());
        var question = (await client.GetFromJsonAsync<JsonElement>($"/questions?jobId={jobId}")).EnumerateArray().Single();
        Assert.Equal("Expired", question.GetProperty("status").GetString());

        // Default: the target's exclusions were honoured, and the run says why.
        var scan = await client.GetFromJsonAsync<JsonElement>($"/scans/{job.GetProperty("resultId").GetString()}");
        Assert.Equal(1, scan.GetProperty("findingCount").GetInt32());
        var labels = await LabelsAsync(client, jobId);
        Assert.Equal(["question", "question_resolved", "state:Running", "note"], labels.Skip(labels.IndexOf("question")).Take(4).ToArray());
    }

    [Fact]
    public async Task Waiting_for_an_answer_does_not_use_up_the_jobs_time_limit()
    {
        using var strict = host.StartVariant("CERBERUS_JOB_TIMEOUT_SECONDS", "0.6");
        var client = await ApiHost.AuthenticatedClientAsync(strict);

        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "svc", sca = false }));
        var jobId = submitted.GetProperty("id").GetString()!;
        await WaitForStateAsync(client, jobId, "AwaitingInput");
        await Task.Delay(1500);   // well past the 0.6 s limit

        var question = (await client.GetFromJsonAsync<JsonElement>($"/questions?status=pending&jobId={jobId}")).EnumerateArray().Single();
        await client.PostAsJsonAsync($"/questions/{question.GetProperty("id").GetString()}/answer", new { selected = new[] { "Honour them" } });

        Assert.Equal("Succeeded", (await WaitForEndAsync(client, jobId)).GetProperty("state").GetString());
    }
}
