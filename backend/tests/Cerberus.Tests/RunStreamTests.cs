using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Jobs;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cerberus.Tests;

public class TextCoalescerTests
{
    [Fact]
    public void Small_deltas_are_held_until_the_chunk_is_long_enough()
    {
        var coalescer = new TextCoalescer(flushAt: 10);

        Assert.Empty(coalescer.Add("a", "1234"));
        Assert.Empty(coalescer.Add("a", "5678"));
        Assert.Equal([("a", "1234567890")], coalescer.Add("a", "90"));
        Assert.Null(coalescer.Flush());
    }

    [Fact]
    public void Flush_returns_what_is_left()
    {
        var coalescer = new TextCoalescer(flushAt: 100);
        coalescer.Add("a", "tail");

        Assert.Equal(("a", "tail"), coalescer.Flush());
        Assert.Null(coalescer.Flush());
    }

    [Fact]
    public void A_new_channel_first_releases_the_previous_one()
    {
        var coalescer = new TextCoalescer(flushAt: 100);
        coalescer.Add("first", "abc");

        Assert.Equal([("first", "abc")], coalescer.Add("second", "xyz"));
        Assert.Equal(("second", "xyz"), coalescer.Flush());
    }

    [Fact]
    public void A_line_end_releases_a_reasonably_sized_chunk_early()
    {
        var coalescer = new TextCoalescer(flushAt: 40);

        Assert.Empty(coalescer.Add("a", "ab\n"));                       // too short to bother
        Assert.Equal([("a", "ab\n0123456789\n")], coalescer.Add("a", "0123456789\n"));
    }

    [Fact]
    public void Nothing_is_lost_or_reordered()
    {
        var coalescer = new TextCoalescer(flushAt: 7);
        var text = string.Concat(Enumerable.Range(0, 50).Select(i => $"w{i} "));
        var emitted = new List<string>();

        for (var i = 0; i < text.Length; i += 3)
        {
            emitted.AddRange(coalescer.Add("a", text.Substring(i, Math.Min(3, text.Length - i))).Select(c => c.Text));
        }
        if (coalescer.Flush() is { } rest) emitted.Add(rest.Text);

        Assert.Equal(text, string.Concat(emitted));
    }
}

[Collection("api")]
public class RunStreamIntegrationTests(ApiHost host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> WaitForJobAsync(HttpClient client, string jobId)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}");
            if (job.GetProperty("state").GetString() is not ("Queued" or "Running" or "AwaitingApproval")) return job;
            Assert.True(DateTime.UtcNow < deadline, "job did not finish in time");
            await Task.Delay(50);
        }
    }

    /// <summary>The stored stream, once its last event is the job's terminal state.</summary>
    private static async Task<List<JsonElement>> StreamAsync(HttpClient client, string jobId)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var page = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}/events");
            var events = page.GetProperty("events").EnumerateArray().ToList();
            if (events.Count > 0 && events[^1].GetProperty("type").GetString() == "state" &&
                events[^1].GetProperty("data").GetProperty("state").GetString() is "Succeeded" or "Failed" or "Cancelled" or "TimedOut")
            {
                return events;
            }
            Assert.True(DateTime.UtcNow < deadline, "the stream did not reach a terminal state");
            await Task.Delay(50);
        }
    }

    private static string Label(JsonElement e)
    {
        var data = e.GetProperty("data");
        return e.GetProperty("type").GetString() switch
        {
            "state" => $"state:{data.GetProperty("state").GetString()}",
            "step" => $"step:{data.GetProperty("id").GetString()}:{data.GetProperty("status").GetString()}",
            var type => type!,
        };
    }

    [Fact]
    public async Task A_scan_job_tells_its_story_in_order()
    {
        var (client, _) = await host.NewUserAsync();
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false }));
        var jobId = submitted.GetProperty("id").GetString()!;

        var events = await StreamAsync(client, jobId);

        Assert.Equal(
        [
            "state:Queued", "state:Running",
            "step:static:running", "finding", "progress", "step:static:completed",
            "step:dependencies:skipped",
            "step:store:running", "step:store:completed",
            "state:Succeeded",
        ], events.Select(Label).ToArray());

        // Sequence numbers are 1..n without gaps, and every event names its job.
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.GetProperty("sequence").GetInt64()));
        Assert.All(events, e => Assert.Equal(jobId, e.GetProperty("jobId").GetString()));

        // The finding arrived on the stream before the job finished, with the full schema.
        var finding = events.Single(e => Label(e) == "finding").GetProperty("data");
        Assert.Equal("crypto", finding.GetProperty("category").GetString());
        Assert.Equal("app/main.py", finding.GetProperty("file").GetString());
        Assert.Equal("1 files scanned, 1 findings.", events.Single(e => Label(e) == "progress").GetProperty("data").GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_stream_can_be_replayed_from_any_point()
    {
        var (client, _) = await host.NewUserAsync();
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false }));
        var jobId = submitted.GetProperty("id").GetString()!;
        var all = await StreamAsync(client, jobId);

        var firstPage = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}/events?limit=3");
        Assert.Equal([1L, 2L, 3L], firstPage.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("sequence").GetInt64()).ToArray());
        Assert.True(firstPage.GetProperty("more").GetBoolean());
        Assert.Equal(all.Count, firstPage.GetProperty("lastSequence").GetInt64());

        var rest = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}/events?after=3");
        Assert.Equal(all.Skip(3).Select(Label), rest.GetProperty("events").EnumerateArray().Select(Label));
        Assert.False(rest.GetProperty("more").GetBoolean());

        var caughtUp = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}/events?after={all.Count}");
        Assert.Equal(0, caughtUp.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task Events_are_pushed_live_in_the_stored_order_and_only_to_the_owner()
    {
        var (alice, aliceToken) = await host.NewUserAsync();
        var (_, bobToken) = await host.NewUserAsync();
        var live = new ConcurrentQueue<JsonElement>();
        var bobLive = new ConcurrentQueue<JsonElement>();
        await using var hub = host.JobsHub(aliceToken);
        await using var bobHub = host.JobsHub(bobToken);
        hub.On<JsonElement>("jobEvent", live.Enqueue);
        bobHub.On<JsonElement>("jobEvent", bobLive.Enqueue);
        await hub.StartAsync();
        await bobHub.StartAsync();

        var submitted = await Json(await alice.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false }));
        var jobId = submitted.GetProperty("id").GetString()!;
        var stored = await StreamAsync(alice, jobId);

        var deadline = DateTime.UtcNow + Patience;
        while (live.Count < stored.Count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"only {live.Count} of {stored.Count} events were pushed");
            await Task.Delay(25);
        }

        Assert.Equal(stored.Select(e => e.GetProperty("sequence").GetInt64()), live.Select(e => e.GetProperty("sequence").GetInt64()));
        Assert.Equal(stored.Select(Label), live.Select(Label));
        Assert.Empty(bobLive);
    }

    [Fact]
    public async Task Triage_streams_the_models_reply_as_text_then_the_verdict()
    {
        var (client, _) = await host.NewUserAsync();
        var scan = await Json(await client.PostAsJsonAsync("/scan", new { path = "svc", sca = false }));
        var submitted = await Json(await client.PostAsJsonAsync("/jobs",
            new { type = "triage", scanId = scan.GetProperty("id").GetString(), model = "echo" }));
        var jobId = submitted.GetProperty("id").GetString()!;

        var events = await StreamAsync(client, jobId);
        var labels = events.Select(Label).ToList();

        Assert.Equal("step:review:running", labels[2]);
        Assert.True(labels.IndexOf("progress") < labels.IndexOf("text"));
        Assert.True(labels.LastIndexOf("text") < labels.IndexOf("triage"));
        Assert.True(labels.IndexOf("triage") < labels.IndexOf("step:review:completed"));
        Assert.Equal("state:Succeeded", labels[^1]);

        // The text events, joined per channel, are exactly what the model wrote about that finding.
        var triage = events.Single(e => Label(e) == "triage").GetProperty("data");
        var findingId = triage.GetProperty("findingId").GetString();
        var text = events.Where(e => Label(e) == "text").Select(e => e.GetProperty("data")).ToList();
        Assert.All(text, t => Assert.Equal(findingId, t.GetProperty("channel").GetString()));
        var reply = string.Concat(text.Select(t => t.GetProperty("text").GetString()));
        Assert.Equal("""{"verdict":"true_positive","reasoning":"pickle.loads runs on data from outside.","remediation":"Use JSON."}""", reply);
        Assert.Equal("TruePositive", triage.GetProperty("verdict").GetString());

        var progress = events.Where(e => Label(e) == "progress").Select(e => e.GetProperty("data")).ToList();
        Assert.Equal((0, 1), (progress[0].GetProperty("done").GetInt32(), progress[0].GetProperty("total").GetInt32()));
        Assert.Equal((1, 1), (progress[^1].GetProperty("done").GetInt32(), progress[^1].GetProperty("total").GetInt32()));
    }

    [Fact]
    public async Task Waiting_for_the_operator_is_part_of_the_stream()
    {
        var (client, _) = await host.NewUserAsync();
        var scan = await Json(await client.PostAsJsonAsync("/scan", new { path = "svc", sca = false }));
        var submitted = await Json(await client.PostAsJsonAsync("/jobs",
            new { type = "triage", scanId = scan.GetProperty("id").GetString(), model = "claude-haiku-4-5" }));
        var jobId = submitted.GetProperty("job").GetProperty("id").GetString()!;

        // While it waits, the stream already shows why.
        var waiting = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}/events");
        Assert.Equal(["state:AwaitingApproval", "approval_requested"], waiting.GetProperty("events").EnumerateArray().Select(Label).ToArray());

        await client.PostAsJsonAsync($"/approvals/{submitted.GetProperty("approval").GetProperty("id").GetString()}/deny", new { note = "no" });
        var events = await StreamAsync(client, jobId);

        Assert.Equal(["state:AwaitingApproval", "approval_requested", "approval_resolved", "state:Cancelled"], events.Select(Label).ToArray());
        Assert.Equal("Denied", events[2].GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_assessment_job_reports_its_steps()
    {
        var (client, _) = await host.NewUserAsync();
        var submitted = await Json(await client.PostAsJsonAsync("/jobs",
            new { type = "assess", catalogId = "owasp-top10", path = "app", sca = false }));

        var events = await StreamAsync(client, submitted.GetProperty("id").GetString()!);

        Assert.Equal(
        [
            "state:Queued", "state:Running",
            "step:findings:running", "step:findings:completed",
            "step:evaluate:running", "progress", "step:evaluate:completed",
            "step:store:running", "step:store:completed",
            "state:Succeeded",
        ], events.Select(Label).ToArray());
        Assert.Contains("non-compliant", events.Single(e => Label(e) == "progress").GetProperty("data").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Another_user_cannot_read_a_runs_stream()
    {
        var (alice, _) = await host.NewUserAsync();
        var (bob, _) = await host.NewUserAsync();
        var submitted = await Json(await alice.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false }));
        var jobId = submitted.GetProperty("id").GetString()!;
        await WaitForJobAsync(alice, jobId);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/jobs/{jobId}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync($"/jobs/{jobId}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.GetAsync("/jobs/missing/events")).StatusCode);
    }
}
