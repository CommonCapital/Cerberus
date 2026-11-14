using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Cerberus.Api.Auth;
using Cerberus.Api.Cluster;
using Cerberus.Api.Jobs;
using Cerberus.Api.Security;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace Cerberus.Tests;

public class LocalClusterTests
{
    [Fact]
    public async Task Places_are_handed_out_up_to_their_number_and_a_single_place_in_the_order_of_arrival()
    {
        var places = new LocalPlaces();

        // Several places (a user's): never more inside than there are places.
        var inside = 0;
        var most = 0;
        async Task ShareAsync()
        {
            await using (await places.EnterAsync("user:1", 2, default))
            {
                var now = Interlocked.Increment(ref inside);
                lock (places) most = Math.Max(most, now);
                await Task.Delay(40);
                Interlocked.Decrement(ref inside);
            }
        }
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ShareAsync()));
        Assert.Equal(2, most);

        // One place (a conversation's): whoever came first goes first.
        var order = new List<int>();
        async Task QueueAsync(int n)
        {
            await using (await places.EnterAsync("session:1", 1, default))
            {
                order.Add(n);
                await Task.Delay(20);
            }
        }
        var waiting = new List<Task>();
        for (var n = 1; n <= 6; n++)
        {
            waiting.Add(QueueAsync(n));
            await Task.Delay(5);   // so that "came first" means something
        }
        await Task.WhenAll(waiting);
        Assert.Equal([1, 2, 3, 4, 5, 6], order);

        // Another key is another set of places.
        await using var held = await places.EnterAsync("session:a", 1, default);
        await using var other = await places.EnterAsync("session:b", 1, default);
    }

    [Fact]
    public async Task Giving_up_the_wait_for_a_place_takes_none_and_a_place_given_back_twice_counts_once()
    {
        var places = new LocalPlaces();
        var first = await places.EnterAsync("k", 1, default);
        using var patience = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => places.EnterAsync("k", 1, patience.Token));
        await first.DisposeAsync();
        await first.DisposeAsync();

        // Exactly one place again — not none (the waiter that gave up), not two (the double release).
        await using var second = await places.EnterAsync("k", 1, default);
        using var soon = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => places.EnterAsync("k", 1, soon.Token));
    }

    [Fact]
    public async Task With_one_instance_there_is_nobody_to_tell()
    {
        var signals = new LocalSignals();
        var heard = new List<string>();

        Assert.False(signals.Distributed);
        await using (await signals.SubscribeAsync(Topics.CancelJob, id =>
                     {
                         heard.Add(id);
                         return Task.CompletedTask;
                     }))
        {
            Assert.True(await signals.SendAsync(Topics.CancelJob, "job-1"));
            Assert.True(await signals.SendAsync(Topics.StopTurn, "turn-1"));   // nobody listens to this one
        }
        await signals.SendAsync(Topics.CancelJob, "job-2");                    // no longer listening

        Assert.Equal(["job-1"], heard);
    }

    [Fact]
    public void Where_redis_is_can_be_said_without_its_password_and_its_address_is_read_in_full()
    {
        var options = new RedisOptions("rediss://worker:made-up%40secret@cache.internal.example:6380");

        Assert.True(options.Enabled);
        Assert.Equal("cache.internal.example:6380", options.Where);
        Assert.DoesNotContain("made-up", options.Where);
        var settings = options.Configuration();
        Assert.Equal(("worker", "made-up@secret", true, false), (settings.User, settings.Password, settings.Ssl, settings.AbortOnConnectFail));
        Assert.Equal("cache.internal.example:6380", Assert.Single(settings.EndPoints).ToString()!.Replace("Unspecified/", ""));

        var plain = new RedisOptions("redis://localhost").Configuration();
        Assert.Equal((null, null, false), (plain.User, plain.Password, plain.Ssl));
        Assert.EndsWith(":6379", Assert.Single(plain.EndPoints).ToString());
        Assert.False(new RedisOptions(null).Enabled);
    }

    [Fact]
    public void A_redis_address_that_cannot_be_understood_stops_the_service_and_is_not_repeated()
    {
        string? Env(string name) => name == "CERBERUS_REDIS_URL" ? "localhost:6379,password=made-up-secret" : null;

        var errors = ServiceConfig.Validate(Env);

        Assert.Equal(["CERBERUS_REDIS_URL must be a redis:// or rediss:// address."], errors);
        Assert.Empty(ServiceConfig.Validate(name => name == "CERBERUS_REDIS_URL" ? "redis://localhost:6379" : null));
    }
}

[Collection("api")]
public class InstanceTests(ApiHost host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private T Service<T>() where T : notnull => host.Services.GetRequiredService<T>();

    private async Task DbAsync(Func<CerberusDbContext, Task> act)
    {
        using var scope = host.Services.CreateScope();
        await act(scope.ServiceProvider.GetRequiredService<CerberusDbContext>());
    }

    /// <summary>A finished scan job of a new user, to be dressed up as running under some instance.</summary>
    private async Task<(HttpClient Client, string JobId)> FinishedJobAsync()
    {
        var (client, _) = await host.NewUserAsync();
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false }));
        var jobId = submitted.GetProperty("id").GetString()!;
        var deadline = DateTime.UtcNow + Patience;
        while ((await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}")).GetProperty("state").GetString() != "Succeeded")
        {
            Assert.True(DateTime.UtcNow < deadline, "the scan did not finish");
            await Task.Delay(25);
        }
        return (client, jobId);
    }

    private Task MarkRunningAsync(string jobId, string? owner) => DbAsync(db => db.Jobs.Where(j => j.Id == jobId)
        .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, nameof(JobState.Running)).SetProperty(j => j.OwnerId, owner).SetProperty(j => j.FinishedAt, (DateTime?)null)));

    [Fact]
    public async Task A_worker_puts_its_name_on_what_it_runs_and_is_on_the_list_of_the_living()
    {
        var (_, jobId) = await FinishedJobAsync();
        var me = Service<InstanceIdentity>().Id;

        await DbAsync(async db =>
        {
            Assert.Equal(me, (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).OwnerId);
            var listed = await db.WorkerInstances.AsNoTracking().SingleAsync(i => i.Id == me);
            Assert.True(DateTime.UtcNow - listed.LastSeenAt < OrphanSweeper.QuietFor);
        });
        Assert.True(await Service<OrphanSweeper>().AliveAsync(default) >= 1);
    }

    [Fact]
    public async Task Work_whose_instance_has_gone_quiet_is_failed_and_work_of_a_living_instance_is_left_alone()
    {
        var (gone, goneJob) = await FinishedJobAsync();
        var (quiet, quietJob) = await FinishedJobAsync();
        var (alive, aliveJob) = await FinishedJobAsync();
        var (legacy, legacyJob) = await FinishedJobAsync();
        var now = DateTime.UtcNow;
        try
        {
            await DbAsync(async db =>
            {
                db.WorkerInstances.Add(new WorkerInstance { Id = "quiet-instance", StartedAt = now.AddHours(-2), LastSeenAt = now - OrphanSweeper.QuietFor - TimeSpan.FromSeconds(5) });
                db.WorkerInstances.Add(new WorkerInstance { Id = "living-instance", StartedAt = now.AddMinutes(-5), LastSeenAt = now });
                // A question the job of the instance that is gone was waiting on.
                var userId = (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == goneJob)).UserId;
                db.OperatorQuestions.Add(new OperatorQuestion { UserId = userId, JobId = goneJob, Text = "Honour the exclusions?", Options = "[]", AllowText = true, ExpiresAt = now.AddHours(1) });
                await db.SaveChangesAsync();
            });
            await MarkRunningAsync(goneJob, "an-instance-that-left");   // said goodbye: not on the list at all
            await MarkRunningAsync(quietJob, "quiet-instance");          // crashed: on the list, silent too long
            await MarkRunningAsync(aliveJob, "living-instance");         // another copy of the service, at work
            await MarkRunningAsync(legacyJob, null);                     // from before instances had names

            var (jobs, _) = await Service<OrphanSweeper>().SweepAsync(default);

            Assert.True(jobs >= 3);
            foreach (var (client, id) in new[] { (gone, goneJob), (quiet, quietJob), (legacy, legacyJob) })
            {
                var job = await client.GetFromJsonAsync<JsonElement>($"/jobs/{id}");
                Assert.Equal(("Failed", OrphanSweeper.Interrupted), (job.GetProperty("state").GetString(), job.GetProperty("error").GetString()));
            }
            Assert.Equal("Running", (await alive.GetFromJsonAsync<JsonElement>($"/jobs/{aliveJob}")).GetProperty("state").GetString());
            // Nobody will ever read the answer to the dead job's question.
            var question = (await gone.GetFromJsonAsync<JsonElement>($"/questions?jobId={goneJob}")).EnumerateArray().Single();
            Assert.Equal("Cancelled", question.GetProperty("status").GetString());
        }
        finally
        {
            await DbAsync(async db =>
            {
                await db.Jobs.Where(j => j.Id == aliveJob).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, nameof(JobState.Succeeded)));
                await db.WorkerInstances.Where(i => i.Id == "quiet-instance" || i.Id == "living-instance").ExecuteDeleteAsync();
            });
        }
    }

    [Fact]
    public async Task Starting_a_second_copy_of_the_service_does_not_fail_what_the_first_is_doing()
    {
        var (client, _) = await host.NewUserAsync();
        var session = (await Json(await client.PostAsJsonAsync("/sessions", new { title = "chat" }))).GetProperty("id").GetString()!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Tools.BeforeReply = async request =>
        {
            if (request.Turns.Any(t => Cerberus.Core.Models.ChatEvents.TextOf(t).Contains("still-here"))) await release.Task.WaitAsync(Patience);
        };
        try
        {
            var accepted = await Json(await client.PostAsJsonAsync("/chat/turns", new
            {
                id = session, messages = new[] { new { id = "u1", role = "user", parts = new[] { new { type = "text", text = "still-here" } } } }, mode = "BUILD", model = "llama3.1",
            }));
            var turnId = accepted.GetProperty("turnId").GetString()!;
            async Task<string> StateAsync() => (await client.GetFromJsonAsync<JsonElement>($"/chat/turns/{turnId}")).GetProperty("state").GetString()!;
            var deadline = DateTime.UtcNow + Patience;
            while (await StateAsync() != "Running") { Assert.True(DateTime.UtcNow < deadline); await Task.Delay(25); }

            // A second instance over the same database starts — and at its start looks for orphaned work.
            using (host.StartVariant("CERBERUS_CHAT_CONCURRENCY", "3"))
            {
                await Task.Delay(200);
                Assert.Equal("Running", await StateAsync());   // before instances had names, this turn was failed here
                release.SetResult();
                while (await StateAsync() == "Running") { Assert.True(DateTime.UtcNow < deadline); await Task.Delay(25); }
                Assert.Equal("Completed", await StateAsync());
            }
        }
        finally
        {
            host.Tools.BeforeReply = null;
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task A_run_two_instances_write_to_keeps_every_event()
    {
        var (client, jobId) = await FinishedJobAsync();
        var stream = Service<RunStream>();
        string userId = "";
        long last = 0;
        await DbAsync(async db =>
        {
            userId = (await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).UserId;
            last = await db.JobEvents.Where(e => e.JobId == jobId).MaxAsync(e => e.Sequence);
        });
        await stream.EmitAsync(jobId, userId, RunEventTypes.Note, new { message = "from this instance" });

        // Another instance writes the next event of the same run (an answer that arrived there, say),
        // unknown to this one, which still remembers the number before it.
        await DbAsync(async db =>
        {
            db.JobEvents.Add(new JobEvent { JobId = jobId, UserId = userId, Sequence = last + 2, Type = RunEventTypes.Note, Data = """{"message":"from the other instance"}""", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
        await stream.EmitAsync(jobId, userId, RunEventTypes.Note, new { message = "from this instance again" });

        var notes = (await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}/events?after={last}")).GetProperty("events").EnumerateArray()
            .Select(e => (e.GetProperty("sequence").GetInt64(), e.GetProperty("data").GetProperty("message").GetString())).ToList();
        Assert.Equal([(last + 1, "from this instance"), (last + 2, "from the other instance"), (last + 3, "from this instance again")], notes);
    }

    [Fact]
    public async Task With_one_instance_nothing_is_said_to_others_and_the_old_answers_hold()
    {
        var (client, jobId) = await FinishedJobAsync();

        // A finished job: cancelling it, or talking to its agent, is refused as before — there is no other
        // instance it could be running in.
        Assert.False(Service<ISignals>().Distributed);
        var cancelled = await client.PostAsync($"/jobs/{jobId}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, cancelled.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/jobs/{jobId}/messages", new { text = "are you there?" })).StatusCode);
        Assert.IsType<LocalPlaces>(Service<IPlaces>());
        Assert.IsType<LoginThrottle>(Service<ILoginThrottle>());
        Assert.DoesNotContain("redis", (await Json(await host.CreateClient().GetAsync("/health/ready"))).GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
    }
}

/// <summary>
/// A real Redis for the tests that are about Redis: started from the machine's own <c>redis-server</c> on a
/// free port, with nothing saved to disk, and stopped afterwards. Where no <c>redis-server</c> is installed
/// those tests are skipped and say so — they cannot be honest against a stand-in, because what they check
/// is what Redis itself does with a script and a subscription.
/// </summary>
public sealed class RedisServer : IDisposable
{
    public static readonly string? Binary = new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin" }
        .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        .Select(dir => Path.Combine(dir, "redis-server"))
        .FirstOrDefault(File.Exists);

    private readonly Process? _process;

    public string Url { get; } = "";

    public RedisServer()
    {
        if (Binary is null) return;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _process = Process.Start(new ProcessStartInfo(Binary)
        {
            ArgumentList = { "--port", port.ToString(), "--bind", "127.0.0.1", "--save", "", "--appendonly", "no" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Url = $"redis://127.0.0.1:{port}";
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                Thread.Sleep(50);
            }
        }
        throw new InvalidOperationException("redis-server did not start.");
    }

    public RedisLink Link() => new(new RedisOptions(Url));

    public void Dispose()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill();
            _process.WaitForExit(2000);
        }
        _process?.Dispose();
    }
}

public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (RedisServer.Binary is null) Skip = "redis-server is not installed on this machine; these tests need a real one.";
    }
}

public sealed class RedisClusterTests(RedisServer redis) : IClassFixture<RedisServer>
{
    [RedisFact]
    public async Task Places_are_counted_over_all_instances()
    {
        await using var a = redis.Link();
        await using var b = redis.Link();
        IPlaces[] instances = [new RedisPlaces(a, NullLogger<RedisPlaces>.Instance), new RedisPlaces(b, NullLogger<RedisPlaces>.Instance)];
        var key = $"user:{Guid.NewGuid():n}";
        var inside = 0;
        var most = 0;

        async Task WorkAsync(int n)
        {
            await using (await instances[n % 2].EnterAsync(key, 2, default))
            {
                var now = Interlocked.Increment(ref inside);
                lock (instances) most = Math.Max(most, now);
                await Task.Delay(120);
                Interlocked.Decrement(ref inside);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(WorkAsync));

        Assert.Equal(2, most);   // two places, whichever instance asked
        // Given back: the set of holders is empty again.
        Assert.Equal(0, await (await a.DatabaseAsync()).SortedSetLengthAsync($"{RedisOptions.Prefix}places:{key}"));
    }

    [RedisFact]
    public async Task A_holder_that_died_loses_its_place_and_one_that_works_on_keeps_it()
    {
        await using var link = redis.Link();
        var places = new RedisPlaces(link, NullLogger<RedisPlaces>.Instance, new RedisPlacesOptions(HoldFor: TimeSpan.FromMilliseconds(600), AskEvery: TimeSpan.FromMilliseconds(40)));
        var db = await link.DatabaseAsync();

        // A holder that took the only place and then died: it is in the set, and will never renew or leave.
        var dead = $"session:{Guid.NewGuid():n}";
        var redisNow = (await (await link.ConnectionAsync()).GetServers()[0].TimeAsync()).ToUniversalTime();
        await db.SortedSetAddAsync($"{RedisOptions.Prefix}places:{dead}", "a-dead-holder", new DateTimeOffset(redisNow).ToUnixTimeMilliseconds() + 600);
        var waited = Stopwatch.StartNew();
        await using (await places.EnterAsync(dead, 1, default))
        {
            Assert.InRange(waited.ElapsedMilliseconds, 300, 3000);   // not at once, and not never
        }

        // A holder that is alive keeps its place for longer than one hold lasts.
        var alive = $"session:{Guid.NewGuid():n}";
        await using (await places.EnterAsync(alive, 1, default))
        {
            using var patience = new CancellationTokenSource(1500);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => places.EnterAsync(alive, 1, patience.Token));
        }
        await using var free = await places.EnterAsync(alive, 1, default);
    }

    [RedisFact]
    public async Task What_one_instance_says_the_others_hear()
    {
        await using var a = redis.Link();
        await using var b = redis.Link();
        var here = new RedisSignals(a, NullLogger<RedisSignals>.Instance);
        var there = new RedisSignals(b, NullLogger<RedisSignals>.Instance);
        var heard = new ConcurrentQueue<string>();
        var arrived = new SemaphoreSlim(0);

        Assert.True(here.Distributed);
        var subscription = await there.SubscribeAsync(Topics.CancelJob, id =>
        {
            heard.Enqueue(id);
            arrived.Release();
            return Task.CompletedTask;
        });
        Assert.True(await here.SendAsync(Topics.CancelJob, "job-7"));
        Assert.True(await here.SendAsync(Topics.StopTurn, "turn-1"));   // another topic: not for this listener
        Assert.True(await arrived.WaitAsync(TimeSpan.FromSeconds(5)));

        await subscription.DisposeAsync();
        await here.SendAsync(Topics.CancelJob, "job-8");
        await Task.Delay(150);

        Assert.Equal(["job-7"], heard);
    }

    [RedisFact]
    public async Task Wrong_passwords_are_counted_together_whichever_instance_they_were_tried_on()
    {
        await using var a = redis.Link();
        await using var b = redis.Link();
        ILoginThrottle here = new RedisLoginThrottle(a, NullLogger<RedisLoginThrottle>.Instance);
        ILoginThrottle there = new RedisLoginThrottle(b, NullLogger<RedisLoginThrottle>.Instance);
        var account = $"{Guid.NewGuid():n}@Example.org";

        // Five failures lock the account; here they are spread over two instances, each of which saw fewer.
        Assert.False(here.Failed(account));
        Assert.False(there.Failed(account));
        Assert.False(here.Failed(account));
        Assert.False(there.Failed(account.ToUpperInvariant()));   // the same account, written differently
        Assert.Null(there.LockedFor(account));
        Assert.True(here.Failed(account));                        // the fifth: locked, and said once

        Assert.InRange(here.LockedFor(account)!.Value, TimeSpan.FromMinutes(4), LoginThrottle.Lock);
        Assert.InRange(there.LockedFor(account)!.Value, TimeSpan.FromMinutes(4), LoginThrottle.Lock);
        Assert.Null(there.LockedFor("somebody-else@example.org"));

        // The account's name is not what is stored.
        var keys = (await a.ConnectionAsync()).GetServers()[0].Keys(pattern: $"{RedisOptions.Prefix}login:*").Select(k => k.ToString()).ToList();
        Assert.NotEmpty(keys);
        Assert.DoesNotContain(keys, k => k.Contains('@') || k.Contains("example", StringComparison.OrdinalIgnoreCase));

        there.Succeeded(account);
        Assert.Null(there.LockedFor(account));
    }

    [RedisFact]
    public async Task A_redis_that_is_away_is_said_plainly_and_nobody_is_let_in_meanwhile()
    {
        // Nothing listens here.
        await using var link = new RedisLink(new RedisOptions("redis://127.0.0.1:1"));

        Assert.StartsWith("Redis cannot be reached", await link.ProblemAsync(default));
        Assert.False(await new RedisSignals(link, NullLogger<RedisSignals>.Instance).SendAsync(Topics.CancelJob, "job-1"));
        using var patience = new CancellationTokenSource(600);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RedisPlaces(link, NullLogger<RedisPlaces>.Instance).EnterAsync("k", 1, patience.Token));
        // The sign-in count goes on, on this instance alone.
        var throttle = new RedisLoginThrottle(link, NullLogger<RedisLoginThrottle>.Instance);
        var locked = false;
        for (var i = 0; i < LoginThrottle.MaxFailures; i++) locked = throttle.Failed("someone@example.org");
        Assert.True(locked);
        Assert.NotNull(throttle.LockedFor("someone@example.org"));
    }
}

/// <summary>Two copies of the service over one database and one Redis: what a request to one must do in the other.</summary>
[Collection("api")]
public sealed class TwoInstanceTests(ApiHost host, RedisServer redis) : IClassFixture<RedisServer>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static object UserMessage(string id, string text) => new { id, role = "user", parts = new[] { new { type = "text", text } } };

    /// <summary>The same signed-in user, talking to another instance.</summary>
    private static HttpClient SameUserOn(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> other, HttpClient signedIn)
    {
        var client = other.CreateClient();
        client.DefaultRequestHeaders.Authorization = signedIn.DefaultRequestHeaders.Authorization;
        return client;
    }

    private static async Task<JsonElement> WaitAsync(HttpClient client, string url, params string[] states)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var found = await client.GetFromJsonAsync<JsonElement>(url);
            if (states.Contains(found.GetProperty("state").GetString())) return found;
            Assert.True(DateTime.UtcNow < deadline, $"{url} is {found.GetProperty("state").GetString()}, expected one of {string.Join("/", states)}");
            await Task.Delay(25);
        }
    }

    [RedisFact]
    public async Task A_reply_written_by_one_instance_reaches_a_client_connected_to_the_other()
    {
        using var first = host.StartVariant("CERBERUS_REDIS_URL", redis.Url);
        using var second = host.StartVariant("CERBERUS_REDIS_URL", redis.Url);
        var onFirst = await ApiHost.AuthenticatedClientAsync(first);
        var onSecond = SameUserOn(second, onFirst);
        var session = (await Json(await onFirst.PostAsJsonAsync("/sessions", new { title = "chat" }))).GetProperty("id").GetString()!;

        var pushed = new ConcurrentQueue<JsonElement>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = ApiHost.HubOf(second, "/hub/chat", onFirst.DefaultRequestHeaders.Authorization!.Parameter);
        hub.On<JsonElement>("chatEvent", e => pushed.Enqueue(e));
        hub.On<JsonElement>("chatTurn", t =>
        {
            if (t.GetProperty("state").GetString() == "Completed") done.TrySetResult();
        });
        await hub.StartAsync();
        await Task.Delay(300);   // until the connection is in its user's group

        // Asked of the first instance, which answers it itself (each has its own queue here)…
        var turnId = (await Json(await onFirst.PostAsJsonAsync("/chat/turns", new { id = session, messages = new[] { UserMessage("u1", "hello") }, mode = "BUILD", model = "llama3.1" })))
            .GetProperty("turnId").GetString()!;
        await done.Task.WaitAsync(Patience);

        // …and heard through the second: every piece, in order.
        var stored = (await onSecond.GetFromJsonAsync<JsonElement>($"/chat/turns/{turnId}/events")).GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(stored.Select(e => e.GetProperty("sequence").GetInt64()), pushed.Where(e => e.GetProperty("turnId").GetString() == turnId).Select(e => e.GetProperty("sequence").GetInt64()));
        Assert.Equal("Echo: hello ", string.Concat(pushed.Select(e => e.GetProperty("chunk")).Where(c => c.GetProperty("type").GetString() == "text-delta").Select(c => c.GetProperty("delta").GetString())));

        // Both say that Redis is there and that they are not alone.
        var ready = await Json(await second.CreateClient().GetAsync("/health/ready"));
        var check = ready.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "redis");
        Assert.Equal("healthy", check.GetProperty("status").GetString());
        Assert.Matches(@"^connected; ([2-9]|\d\d+) instance\(s\) of the service alive$", check.GetProperty("detail").GetString());
    }

    [RedisFact]
    public async Task A_turn_being_answered_by_one_instance_can_be_stopped_through_the_other_and_keeps_its_place_in_line()
    {
        using var first = host.StartVariant("CERBERUS_REDIS_URL", redis.Url);
        using var second = host.StartVariant("CERBERUS_REDIS_URL", redis.Url);
        var onFirst = await ApiHost.AuthenticatedClientAsync(first);
        var onSecond = SameUserOn(second, onFirst);
        var session = (await Json(await onFirst.PostAsJsonAsync("/sessions", new { title = "chat" }))).GetProperty("id").GetString()!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Tools.BeforeReply = async request =>
        {
            if (request.Turns.Any(t => Cerberus.Core.Models.ChatEvents.TextOf(t).Contains("across-instances"))) await release.Task.WaitAsync(Patience);
        };
        try
        {
            async Task<string> SubmitAsync(HttpClient client, string id, string text) =>
                (await Json(await client.PostAsJsonAsync("/chat/turns", new { id = session, messages = new[] { UserMessage(id, text) }, mode = "BUILD", model = "llama3.1" })))
                .GetProperty("turnId").GetString()!;

            var running = await SubmitAsync(onFirst, "u1", "across-instances");
            await WaitAsync(onFirst, $"/chat/turns/{running}", "Running");

            // The next turn of the same conversation goes to the other instance — and waits there: one at a time, over both.
            var next = await SubmitAsync(onSecond, "u2", "after it");
            await Task.Delay(400);
            Assert.Equal("Queued", (await onSecond.GetFromJsonAsync<JsonElement>($"/chat/turns/{next}")).GetProperty("state").GetString());

            // Stopped through the instance that is not answering it.
            var stop = await onSecond.PostAsync($"/chat/turns/{running}/cancel", null);
            Assert.Equal(HttpStatusCode.Accepted, stop.StatusCode);
            var stopped = await WaitAsync(onSecond, $"/chat/turns/{running}", "Cancelled", "Completed", "Failed");
            Assert.Equal(("Cancelled", "Stopped by the user."), (stopped.GetProperty("state").GetString(), stopped.GetProperty("error").GetString()));

            // And the one that waited is answered now, by the instance it was sent to.
            var answered = await WaitAsync(onSecond, $"/chat/turns/{next}", "Completed", "Failed", "Cancelled");
            Assert.Equal("Completed", answered.GetProperty("state").GetString());
        }
        finally
        {
            host.Tools.BeforeReply = null;
            release.TrySetResult();
        }
    }

    [RedisFact]
    public async Task A_question_asked_by_a_job_in_one_instance_can_be_answered_through_the_other_and_the_job_stopped_there_too()
    {
        using var first = host.StartVariant("CERBERUS_REDIS_URL", redis.Url);
        using var second = host.StartVariant("CERBERUS_REDIS_URL", redis.Url);
        var onFirst = await ApiHost.AuthenticatedClientAsync(first);
        var onSecond = SameUserOn(second, onFirst);

        // The scan of this fixture asks whether to honour the target's own exclusions, and waits.
        var jobId = (await Json(await onFirst.PostAsJsonAsync("/jobs", new { type = "scan", path = "svc", sca = false }))).GetProperty("id").GetString()!;
        await WaitAsync(onFirst, $"/jobs/{jobId}", "AwaitingInput");
        var question = (await onSecond.GetFromJsonAsync<JsonElement>($"/questions?status=pending&jobId={jobId}")).EnumerateArray().Single();

        var answered = await onSecond.PostAsJsonAsync($"/questions/{question.GetProperty("id").GetString()}/answer", new { selected = new[] { "Scan everything" } });
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        Assert.Equal("Succeeded", (await WaitAsync(onSecond, $"/jobs/{jobId}", "Succeeded", "Failed", "Cancelled", "TimedOut")).GetProperty("state").GetString());

        // The same again, and this time the waiting job is cancelled through the other instance.
        var another = (await Json(await onFirst.PostAsJsonAsync("/jobs", new { type = "scan", path = "svc", sca = false }))).GetProperty("id").GetString()!;
        await WaitAsync(onFirst, $"/jobs/{another}", "AwaitingInput");
        var cancel = await onSecond.PostAsync($"/jobs/{another}/cancel", null);
        Assert.True(cancel.IsSuccessStatusCode, $"cancel answered {(int)cancel.StatusCode}");
        Assert.Equal("Cancelled", (await WaitAsync(onSecond, $"/jobs/{another}", "Cancelled", "Succeeded", "Failed", "TimedOut")).GetProperty("state").GetString());
    }
}
