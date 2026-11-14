using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api.Jobs;
using Cerberus.Api.Security;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Tests;

/// <summary>Whose turn it is, decided on a database of its own — no worker is running here to take the jobs away.</summary>
public sealed class JobQueueChoiceTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"cerberus-queue-{Guid.NewGuid():n}.db");
    private readonly CerberusDbContext _db;
    private readonly DateTime _start = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
    private int _minute;

    public JobQueueChoiceTests()
    {
        _db = new CerberusDbContext(new DbContextOptionsBuilder<CerberusDbContext>().UseSqlite($"Data Source={_path}").Options);
        _db.Database.EnsureCreated();
        foreach (var name in new[] { "alice", "bob", "carol" })
        {
            _db.Users.Add(new User { Id = name, Email = $"{name}@test.dev", PasswordHash = "x" });
        }
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    /// <summary>Adds jobs of one user in the given states, each a minute after the one before (over all users).</summary>
    private List<string> Jobs(string user, params JobState[] states)
    {
        var ids = new List<string>();
        foreach (var state in states)
        {
            var job = new Job { UserId = user, Type = "scan", TargetPath = ".", State = state.ToString(), CreatedAt = _start.AddMinutes(_minute++) };
            _db.Jobs.Add(job);
            ids.Add(job.Id);
        }
        _db.SaveChanges();
        return ids;
    }

    private async Task<(string? User, string? Id)> NextAsync(int perUser = 0) =>
        await JobQueue.NextAsync(_db, perUser, default) is { } job ? (job.UserId, job.Id) : (null, null);

    [Fact]
    public async Task One_persons_batch_does_not_stand_in_front_of_another_persons_single_run()
    {
        // Alice queued twenty and has one running; Bob comes afterwards with one.
        Jobs("alice", JobState.Running);
        Jobs("alice", Enumerable.Repeat(JobState.Queued, 20).ToArray());
        var bobs = Jobs("bob", JobState.Queued);

        Assert.Equal(("bob", bobs[0]), await NextAsync());   // first-come would have made him wait for all twenty
    }

    [Fact]
    public async Task Between_people_with_equally_few_running_the_one_who_waited_longest_goes_first_and_each_persons_own_go_in_order()
    {
        var alices = Jobs("alice", JobState.Queued);
        var bobs = Jobs("bob", JobState.Queued, JobState.Queued);
        var later = Jobs("alice", JobState.Queued);

        Assert.Equal(("alice", alices[0]), await NextAsync());   // nobody runs anything: Alice asked first

        await _db.Jobs.Where(j => j.Id == alices[0]).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, nameof(JobState.Running)));
        Assert.Equal(("bob", bobs[0]), await NextAsync());       // now Alice has one running and Bob none

        await _db.Jobs.Where(j => j.Id == bobs[0]).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, nameof(JobState.Running)));
        Assert.Equal(("bob", bobs[1]), await NextAsync());       // one each: Bob's second has waited longer than Alice's second
        Assert.NotEqual(later[0], (await NextAsync()).Id);
    }

    [Fact]
    public async Task With_one_worker_the_places_go_round_in_turn_and_not_first_come()
    {
        // Alice's first run has just finished; her five others waited before Bob's one. Nobody runs anything:
        // the moment a single worker comes free. First-come would give the place to Alice again.
        var done = Jobs("alice", JobState.Succeeded);
        await _db.Jobs.Where(j => j.Id == done[0]).ExecuteUpdateAsync(s => s.SetProperty(j => j.StartedAt, _start.AddHours(1)));
        var alices = Jobs("alice", Enumerable.Repeat(JobState.Queued, 5).ToArray());
        var bobs = Jobs("bob", JobState.Queued);

        Assert.Equal(("bob", bobs[0]), await NextAsync());

        // Bob's starts and finishes; now Alice's turn has come round again.
        await _db.Jobs.Where(j => j.Id == bobs[0]).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.State, nameof(JobState.Succeeded)).SetProperty(j => j.StartedAt, _start.AddHours(2)));
        Assert.Equal(("alice", alices[0]), await NextAsync());

        // Carol, who has never had a place, goes before both.
        var carols = Jobs("carol", JobState.Queued);
        Assert.Equal(("carol", carols[0]), await NextAsync());
    }

    [Fact]
    public async Task A_job_that_waits_for_its_operators_answer_still_holds_its_worker()
    {
        Jobs("alice", JobState.AwaitingInput);
        var alices = Jobs("alice", JobState.Queued);
        var carols = Jobs("carol", JobState.Queued);

        Assert.Equal(("carol", carols[0]), await NextAsync());
        Assert.NotEqual(alices[0], (await NextAsync()).Id);
    }

    [Fact]
    public async Task One_person_alone_may_use_every_worker_unless_a_limit_per_person_is_set()
    {
        Jobs("alice", JobState.Running, JobState.Running);
        var waiting = Jobs("alice", JobState.Queued);

        Assert.Equal(("alice", waiting[0]), await NextAsync());          // nobody else is waiting for the place
        Assert.Equal((null, null), await NextAsync(perUser: 2));         // at her limit: passed over although a worker is free
        Assert.Equal(("alice", waiting[0]), await NextAsync(perUser: 3));

        // With a limit, a free place goes to someone below theirs.
        var bobs = Jobs("bob", JobState.Queued);
        Assert.Equal(("bob", bobs[0]), await NextAsync(perUser: 2));
    }

    [Fact]
    public async Task What_does_not_wait_is_never_chosen()
    {
        Assert.Equal((null, null), await NextAsync());
        Jobs("alice", JobState.AwaitingApproval, JobState.Running, JobState.Succeeded, JobState.Cancelled, JobState.Failed);

        Assert.Equal((null, null), await NextAsync());   // a job that still needs approval is not in the queue yet
    }

    [Fact]
    public void A_limit_that_is_not_a_number_stops_the_service()
    {
        Assert.Equal(["CERBERUS_JOB_PER_USER must be a whole number."], ServiceConfig.Validate(name => name == "CERBERUS_JOB_PER_USER" ? "two" : null));
        Assert.Empty(ServiceConfig.Validate(name => name == "CERBERUS_JOB_PER_USER" ? "2" : null));
    }
}

[Collection("api")]
public class FairQueueApiTests(ApiHost host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);
    private const string Marker = "hold-this-review";

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> SubmitAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "svc", model = "llama3.1", instructions = Marker });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private static async Task<JsonElement> JobAsync(HttpClient client, string id) => await client.GetFromJsonAsync<JsonElement>($"/jobs/{id}");

    private static async Task<string> StateAsync(HttpClient client, string id) => (await JobAsync(client, id)).GetProperty("state").GetString()!;

    private static async Task WaitAsync(Func<Task<bool>> until, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await until())
        {
            Assert.True(DateTime.UtcNow < deadline, what);
            await Task.Delay(30);
        }
    }

    /// <summary>Holds each marked review at its very first step until a place is handed out; later steps pass.</summary>
    private SemaphoreSlim HoldReviews()
    {
        var passes = new SemaphoreSlim(0);
        host.Tools.BeforeReply = async request =>
        {
            if (request.Turns.Count == 1 && Cerberus.Core.Models.ChatEvents.TextOf(request.Turns[0]).Contains(Marker)) await passes.WaitAsync();
        };
        return passes;
    }

    [Fact]
    public async Task While_one_person_has_a_batch_waiting_another_persons_run_gets_the_next_free_place()
    {
        var (alice, _) = await host.NewUserAsync();
        var (bob, _) = await host.NewUserAsync();
        var passes = HoldReviews();
        try
        {
            // Alice fills both workers and has three more waiting.
            var alices = new List<string>();
            for (var i = 0; i < 5; i++) alices.Add(await SubmitAsync(alice));
            await WaitAsync(async () => (await Task.WhenAll(alices.Select(id => StateAsync(alice, id)))).Count(s => s == "Running") == 2, "two of Alice's jobs never started");

            // Bob comes last. First-come, he would be sixth.
            var bobs = await SubmitAsync(bob);
            var waiting = await JobAsync(bob, bobs);
            Assert.Equal("Queued", waiting.GetProperty("state").GetString());
            Assert.Equal((0, 1), (waiting.GetProperty("queue").GetProperty("ahead").GetInt32(), waiting.GetProperty("queue").GetProperty("others").GetInt32()));
            // Alice's last one knows what is before it: her two running and her two queued earlier.
            var last = await JobAsync(alice, alices[4]);
            Assert.Equal((4, 1), (last.GetProperty("queue").GetProperty("ahead").GetInt32(), last.GetProperty("queue").GetProperty("others").GetInt32()));

            // One of Alice's finishes. The place goes to Bob, not to her third.
            passes.Release();
            await WaitAsync(async () => await StateAsync(bob, bobs) is "Running" or "Succeeded", "Bob's job did not get the free place");
            Assert.Equal(3, (await Task.WhenAll(alices.Select(id => StateAsync(alice, id)))).Count(s => s == "Queued"));
            Assert.False((await JobAsync(bob, bobs)).TryGetProperty("queue", out _));   // it no longer waits

            // Everything runs to its end, nothing is left behind.
            passes.Release(20);
            await WaitAsync(async () => (await Task.WhenAll(alices.Select(id => StateAsync(alice, id)))).All(s => s == "Succeeded") && await StateAsync(bob, bobs) == "Succeeded",
                "not every job finished");
        }
        finally
        {
            host.Tools.BeforeReply = null;
            passes.Release(100);
        }
    }

    [Fact]
    public async Task With_a_limit_per_person_a_free_worker_stays_free_and_nothing_is_left_waiting_for_ever()
    {
        using var limited = host.StartVariant("CERBERUS_JOB_PER_USER", "1");
        var client = await ApiHost.AuthenticatedClientAsync(limited);
        var passes = HoldReviews();
        try
        {
            var jobs = new List<string>();
            for (var i = 0; i < 3; i++) jobs.Add(await SubmitAsync(client));
            await WaitAsync(async () => (await Task.WhenAll(jobs.Select(id => StateAsync(client, id)))).Any(s => s == "Running"), "no job started");

            // Two workers, but one place per person: the second worker stays free.
            await Task.Delay(400);
            var states = await Task.WhenAll(jobs.Select(id => StateAsync(client, id)));
            Assert.Equal((1, 2), (states.Count(s => s == "Running"), states.Count(s => s == "Queued")));

            // As each finishes, the next of the same person starts — although no new message announces it.
            passes.Release(20);
            await WaitAsync(async () => (await Task.WhenAll(jobs.Select(id => StateAsync(client, id)))).All(s => s == "Succeeded"), "a job was left waiting");
        }
        finally
        {
            host.Tools.BeforeReply = null;
            passes.Release(100);
        }
    }

    [Fact]
    public async Task What_others_have_waiting_is_a_number_and_nothing_more()
    {
        var (alice, _) = await host.NewUserAsync();
        var (bob, _) = await host.NewUserAsync();
        var passes = HoldReviews();
        try
        {
            var alices = new List<string>();
            for (var i = 0; i < 3; i++) alices.Add(await SubmitAsync(alice));
            var bobs = await SubmitAsync(bob);

            // Bob learns that one other person has work in the queue — not who, not what, and he cannot open it.
            await WaitAsync(async () => await StateAsync(bob, bobs) is "Queued" or "Running", "Bob's job was not accepted");
            Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/jobs/{alices[0]}")).StatusCode);
            Assert.DoesNotContain(alices, id => (bob.GetStringAsync($"/jobs/{bobs}").Result).Contains(id));
            Assert.Equal(1, (await bob.GetFromJsonAsync<JsonElement>("/jobs")).GetArrayLength());
        }
        finally
        {
            host.Tools.BeforeReply = null;
            passes.Release(100);
            await WaitAsync(async () => await StateAsync(bob, (await bob.GetFromJsonAsync<JsonElement>("/jobs"))[0].GetProperty("id").GetString()!) == "Succeeded", "cleanup: Bob's job did not finish");
            await WaitAsync(async () => (await alice.GetFromJsonAsync<JsonElement>("/jobs")).EnumerateArray().All(j => j.GetProperty("state").GetString() == "Succeeded"), "cleanup: Alice's jobs did not finish");
        }
    }
}
