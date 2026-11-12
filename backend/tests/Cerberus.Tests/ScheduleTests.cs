using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Cerberus.Tests;

public class ScheduleRulesTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("90", 90)]
    [InlineData("\"90m\"", 90)]
    [InlineData("\"6h\"", 360)]
    [InlineData("\" 1D \"", 1440)]
    [InlineData("\"1w\"", 10080)]
    [InlineData("\"30d\"", 43200)]
    public void An_interval_is_minutes_or_a_count_with_a_unit(string every, int minutes)
    {
        Assert.Equal(minutes, ScheduleRunner.ParseInterval(J(every)));
    }

    [Theory]
    [InlineData("\"10m\"")]      // more often than every quarter of an hour is not a schedule, it is a loop
    [InlineData("14")]
    [InlineData("\"31d\"")]
    [InlineData("\"soon\"")]
    [InlineData("\"-6h\"")]
    [InlineData("\"1.5h\"")]
    [InlineData("\"h\"")]
    [InlineData("true")]
    [InlineData("\"99999999999h\"")]
    public void Anything_else_is_not_an_interval(string every)
    {
        Assert.Null(ScheduleRunner.ParseInterval(J(every)));
        Assert.Null(ScheduleRunner.ParseInterval(null));
    }

    [Fact]
    public void An_interval_reads_in_its_largest_whole_unit()
    {
        Assert.Equal("every 45 minutes", ScheduleRunner.DescribeInterval(45));
        Assert.Equal("every hour", ScheduleRunner.DescribeInterval(60));
        Assert.Equal("every 6 hours", ScheduleRunner.DescribeInterval(360));
        Assert.Equal("every day", ScheduleRunner.DescribeInterval(1440));
        Assert.Equal("every 2 weeks", ScheduleRunner.DescribeInterval(20160));
        Assert.Equal("every 36 hours", ScheduleRunner.DescribeInterval(2160));
    }

    [Fact]
    public void After_downtime_the_next_run_is_the_next_point_on_the_schedules_own_grid()
    {
        var due = new DateTime(2026, 11, 12, 2, 0, 0, DateTimeKind.Utc);

        Assert.Equal(due, ScheduleRunner.NextAfter(due, 60, due.AddMinutes(-5)));              // not due yet: unchanged
        Assert.Equal(due.AddHours(1), ScheduleRunner.NextAfter(due, 60, due));                 // fired on time
        Assert.Equal(due.AddHours(1), ScheduleRunner.NextAfter(due, 60, due.AddMinutes(20)));
        Assert.Equal(due.AddHours(4), ScheduleRunner.NextAfter(due, 60, due.AddMinutes(210))); // three runs missed: not made up for
    }
}

[Collection("api")]
public class ScheduleApiTests(ApiHost host)
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private ScheduleRunner Runner => host.Services.GetRequiredService<ScheduleRunner>();

    private static object ScanOf(string path) => new { type = "scan", path, sca = false, exclusions = "honour" };

    private static async Task<JsonElement> CreateAsync(HttpClient client, object job, object every, string? name = null)
    {
        var response = await client.PostAsJsonAsync("/schedules", new { name, every, job });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await Json(response);
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string id) => await Json(await client.GetAsync($"/schedules/{id}"));

    private static async Task<List<JsonElement>> JobsAsync(HttpClient client) =>
        (await Json(await client.GetAsync("/jobs?limit=50"))).EnumerateArray().ToList();

    private static async Task<string> FinishedAsync(HttpClient client, string jobId)
    {
        for (var i = 0; i < 200; i++)
        {
            var state = (await Json(await client.GetAsync($"/jobs/{jobId}"))).GetProperty("state").GetString()!;
            if (state is "Succeeded" or "Failed" or "Cancelled" or "TimedOut") return state;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Job {jobId} did not finish.");
    }

    /// <summary>A moment at which this schedule is due, as the scheduler would see it.</summary>
    private static DateTime Due(JsonElement schedule, int minutesLate = 1) => schedule.GetProperty("nextRunAt").GetDateTime().ToUniversalTime().AddMinutes(minutesLate);

    [Fact]
    public async Task A_schedule_is_a_job_request_and_an_interval_and_belongs_to_its_owner()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();

        var created = await CreateAsync(client, ScanOf("svc"), "1d", "  Nightly\tscan ");

        var id = created.GetProperty("id").GetString()!;
        Assert.Equal(("Nightly scan", "scan", "svc", 1440, "every day", true),
            (created.GetProperty("name").GetString(), created.GetProperty("type").GetString(), created.GetProperty("targetPath").GetString(),
             created.GetProperty("everyMinutes").GetInt32(), created.GetProperty("every").GetString(), created.GetProperty("enabled").GetBoolean()));
        // The first run is one interval away unless a time is given.
        var next = created.GetProperty("nextRunAt").GetDateTime().ToUniversalTime();
        Assert.InRange(next, DateTime.UtcNow.AddHours(23.9), DateTime.UtcNow.AddHours(24.1));
        Assert.Equal(JsonValueKind.Null, created.GetProperty("lastRunAt").ValueKind);

        Assert.Equal(id, Assert.Single((await Json(await client.GetAsync("/schedules"))).EnumerateArray()).GetProperty("id").GetString());
        Assert.Empty((await Json(await other.GetAsync("/schedules"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/schedules/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/schedules/{id}", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/schedules/{id}/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/schedules/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/schedules")).StatusCode);

        // A name is optional: it then says what runs.
        Assert.Equal("review of svc", (await CreateAsync(client, new { type = "review", path = "svc" }, 360)).GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/schedules/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/schedules/{id}")).StatusCode);
    }

    [Fact]
    public async Task What_cannot_be_a_schedule_is_refused_when_it_is_made()
    {
        var (client, _) = await host.NewUserAsync();
        async Task<string> Refused(object body)
        {
            var response = await client.PostAsJsonAsync("/schedules", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return (await Json(response)).GetProperty("error").GetString()!;
        }

        Assert.Contains("job is required", await Refused(new { every = "1d" }));
        Assert.Contains("every must be", await Refused(new { every = "5m", job = ScanOf("svc") }));
        Assert.Contains("every must be", await Refused(new { job = ScanOf("svc") }));
        // A triage or an evidence run works on one scan or one assessment: there is nothing to repeat.
        Assert.Contains("A triage job cannot be scheduled", await Refused(new { every = "1d", job = new { type = "triage", scanId = "x", model = "echo" } }));
        Assert.Contains("A evidence job cannot be scheduled", await Refused(new { every = "1d", job = new { type = "evidence", assessmentId = "x", model = "echo" } }));
        Assert.Contains("scans afresh", await Refused(new { every = "1d", job = new { type = "assess", catalogId = "owasp-top10-2021", scanId = "x" } }));
        Assert.Contains("in the past", await Refused(new { every = "1d", firstRunAt = DateTime.UtcNow.AddHours(-2), job = ScanOf("svc") }));
        Assert.Contains("more than a year", await Refused(new { every = "1d", firstRunAt = DateTime.UtcNow.AddDays(800), job = ScanOf("svc") }));
    }

    [Fact]
    public async Task A_due_schedule_starts_the_job_as_its_owner_once_and_moves_on()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();
        var schedule = await CreateAsync(client, ScanOf("svc"), "1h");
        var id = schedule.GetProperty("id").GetString()!;
        var first = schedule.GetProperty("nextRunAt").GetDateTime().ToUniversalTime();

        // Not due yet: nothing happens.
        await Runner.RunDueAsync(first.AddMinutes(-10), default);
        Assert.Empty(await JobsAsync(client));

        // Due — and two instances of the service notice at the same moment. One of them fires it.
        var now = first.AddMinutes(1);
        await Task.WhenAll(Runner.RunDueAsync(now, default), Runner.RunDueAsync(now, default));

        var job = Assert.Single(await JobsAsync(client));
        Assert.Equal(("scan", "svc"), (job.GetProperty("type").GetString(), job.GetProperty("targetPath").GetString()));
        Assert.Empty(await JobsAsync(other));
        Assert.Equal("Succeeded", await FinishedAsync(client, job.GetProperty("id").GetString()!));

        var after = await GetAsync(client, id);
        Assert.Equal(("Started.", job.GetProperty("id").GetString()), (after.GetProperty("lastOutcome").GetString(), after.GetProperty("lastJobId").GetString()));
        Assert.Equal(first.AddHours(1), after.GetProperty("nextRunAt").GetDateTime().ToUniversalTime());

        // Looking again at the same moment does not start it again.
        await Runner.RunDueAsync(now, default);
        Assert.Single(await JobsAsync(client));

        // The next hour, it runs again.
        await Runner.RunDueAsync(first.AddHours(1).AddMinutes(1), default);
        Assert.Equal(2, (await JobsAsync(client)).Count);

        // It is on the record who did what.
        var audit = await (await client.GetAsync("/audit")).Content.ReadAsStringAsync();
        Assert.Contains("schedule.create", audit);
        Assert.Contains("schedule.run", audit);
    }

    [Fact]
    public async Task After_downtime_a_schedule_fires_once_not_once_per_missed_run()
    {
        var (client, _) = await host.NewUserAsync();
        var schedule = await CreateAsync(client, ScanOf("svc"), "1h");
        var first = schedule.GetProperty("nextRunAt").GetDateTime().ToUniversalTime();

        // The service was down for five and a half intervals.
        await Runner.RunDueAsync(first.AddMinutes(330), default);

        Assert.Single(await JobsAsync(client));
        var after = await GetAsync(client, schedule.GetProperty("id").GetString()!);
        Assert.Equal(first.AddHours(6), after.GetProperty("nextRunAt").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task A_scheduled_run_asks_for_approval_like_any_other_and_always_allow_works_for_it_like_for_any_other()
    {
        var (client, _) = await host.NewUserAsync();
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/settings/permissions", new { mode = "ask" })).StatusCode);
        var schedule = await CreateAsync(client, ScanOf("svc"), "1h");
        var id = schedule.GetProperty("id").GetString()!;
        var first = schedule.GetProperty("nextRunAt").GetDateTime().ToUniversalTime();

        // 1. Due. The operator's policy says every run is asked about — so this one waits, and says so.
        await Runner.RunDueAsync(first.AddMinutes(1), default);
        var waiting = Assert.Single(await JobsAsync(client));
        Assert.Equal("AwaitingApproval", waiting.GetProperty("state").GetString());
        Assert.StartsWith("Waiting for your approval: Run a scan of \"svc\"", (await GetAsync(client, id)).GetProperty("lastOutcome").GetString());

        // 2. The next hour comes and nobody has answered: runs do not pile up behind the question.
        await Runner.RunDueAsync(first.AddHours(1).AddMinutes(1), default);
        Assert.Single(await JobsAsync(client));
        Assert.Equal("Skipped: the previous run is still waiting for approval.", (await GetAsync(client, id)).GetProperty("lastOutcome").GetString());

        // 3. The operator approves, and chooses "always allow" — the ordinary answer to the ordinary question.
        var approvalId = waiting.GetProperty("approvalId").GetString();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/approvals/{approvalId}/approve", new { remember = true })).StatusCode);
        Assert.Equal("Succeeded", await FinishedAsync(client, waiting.GetProperty("id").GetString()!));

        // 4. From then on the schedule's runs start without asking.
        await Runner.RunDueAsync(first.AddHours(2).AddMinutes(1), default);
        var jobs = await JobsAsync(client);
        Assert.Equal(2, jobs.Count);
        Assert.DoesNotContain(jobs, j => j.GetProperty("state").GetString() == "AwaitingApproval");
        Assert.Equal("Started.", (await GetAsync(client, id)).GetProperty("lastOutcome").GetString());
    }

    [Fact]
    public async Task A_schedule_the_server_keeps_refusing_is_paused_and_says_why()
    {
        var (client, _) = await host.NewUserAsync();
        // Accepted when made (catalogs can come and go), refused each time it is submitted.
        var schedule = await CreateAsync(client, new { type = "review", path = "svc", catalogs = new[] { "no-such-catalog" } }, "1h");
        var id = schedule.GetProperty("id").GetString()!;
        var first = schedule.GetProperty("nextRunAt").GetDateTime().ToUniversalTime();

        await Runner.RunDueAsync(first.AddMinutes(1), default);
        var once = await GetAsync(client, id);
        Assert.Equal("Refused: Unknown catalog 'no-such-catalog'", once.GetProperty("lastOutcome").GetString());
        Assert.True(once.GetProperty("enabled").GetBoolean());

        await Runner.RunDueAsync(first.AddHours(1).AddMinutes(1), default);
        await Runner.RunDueAsync(first.AddHours(2).AddMinutes(1), default);

        var paused = await GetAsync(client, id);
        Assert.False(paused.GetProperty("enabled").GetBoolean());
        Assert.Equal("Paused after 3 refusals in a row. Last: Refused: Unknown catalog 'no-such-catalog'", paused.GetProperty("lastOutcome").GetString());
        Assert.Empty(await JobsAsync(client));

        // Paused means paused.
        await Runner.RunDueAsync(first.AddHours(9), default);
        Assert.Equal(paused.GetProperty("lastRunAt").GetDateTime(), (await GetAsync(client, id)).GetProperty("lastRunAt").GetDateTime());
    }

    [Fact]
    public async Task A_paused_schedule_does_not_run_and_resuming_does_not_fire_for_the_time_it_was_paused()
    {
        var (client, _) = await host.NewUserAsync();
        var schedule = await CreateAsync(client, ScanOf("svc"), "1h", "hourly");
        var id = schedule.GetProperty("id").GetString()!;
        var first = schedule.GetProperty("nextRunAt").GetDateTime().ToUniversalTime();

        Assert.False((await Json(await client.PutAsJsonAsync($"/schedules/{id}", new { enabled = false }))).GetProperty("enabled").GetBoolean());
        await Runner.RunDueAsync(first.AddHours(3), default);
        Assert.Empty(await JobsAsync(client));

        var resumed = await Json(await client.PutAsJsonAsync($"/schedules/{id}", new { enabled = true, name = "hourly scan" }));
        Assert.True(resumed.GetProperty("enabled").GetBoolean());
        Assert.Equal("hourly scan", resumed.GetProperty("name").GetString());
        Assert.True(resumed.GetProperty("nextRunAt").GetDateTime().ToUniversalTime() >= first);

        // A new interval starts from now.
        var changed = await Json(await client.PutAsJsonAsync($"/schedules/{id}", new { every = "6h" }));
        Assert.Equal("every 6 hours", changed.GetProperty("every").GetString());
        Assert.InRange(changed.GetProperty("nextRunAt").GetDateTime().ToUniversalTime(), DateTime.UtcNow.AddHours(5.9), DateTime.UtcNow.AddHours(6.1));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/schedules/{id}", new { every = "1m" })).StatusCode);
    }

    [Fact]
    public async Task Run_now_fires_at_once_and_leaves_the_rhythm_alone()
    {
        var (client, _) = await host.NewUserAsync();
        var schedule = await CreateAsync(client, ScanOf("svc"), "1d");
        var id = schedule.GetProperty("id").GetString()!;

        var response = await client.PostAsync($"/schedules/{id}/run", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fired = await Json(response);
        Assert.Equal(("started", "Started."), (fired.GetProperty("outcome").GetString(), fired.GetProperty("detail").GetString()));
        Assert.Equal("Succeeded", await FinishedAsync(client, fired.GetProperty("jobId").GetString()!));
        Assert.Equal(schedule.GetProperty("nextRunAt").GetDateTime(), (await GetAsync(client, id)).GetProperty("nextRunAt").GetDateTime());
    }
}
