using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api.Cluster;
using Cerberus.Api.Hosting;
using Cerberus.Api.Jobs;
using Cerberus.Api.Security;
using Cerberus.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cerberus.Tests;

/// <summary>A service as the start-up decision sees it, answering as the test says.</summary>
internal sealed class FakeService(string name, bool configured, Func<int, Task> answer) : IServiceProbe
{
    public int Attempts;
    public string Name => name;
    public string Variable => "CERBERUS_" + name.ToUpperInvariant() + "_URL";
    public bool Configured => configured;
    public string Where => "somewhere:1";
    public string StandIn => "its stand-in";
    public Task ProbeAsync(CancellationToken ct) => answer(Interlocked.Increment(ref Attempts));
}

/// <summary>How the API decides, at start-up, what it runs on: what is there is used, what is not is stood in for.</summary>
public sealed class BackingServicesTests
{
    private static Task Down(int _) => throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused);

    [Fact]
    public async Task A_service_that_is_not_configured_is_not_even_tried()
    {
        var service = new FakeService("Redis", configured: false, Down);
        var backing = await BackingServices.DecideAsync([service], TimeSpan.FromSeconds(5), default);

        Assert.Equal(0, service.Attempts);
        var choice = backing["Redis"];
        Assert.False(choice.InUse);
        Assert.False(choice.FellBack);
        Assert.Equal("Redis: not configured (CERBERUS_REDIS_URL); using its stand-in", choice.Describe());
    }

    [Fact]
    public async Task A_service_that_answers_is_used()
    {
        var backing = await BackingServices.DecideAsync([new FakeService("PostgreSQL", true, _ => Task.CompletedTask)], TimeSpan.FromSeconds(5), default);

        Assert.True(backing.Uses("PostgreSQL"));
        Assert.Empty(backing.FellBack);
        Assert.Equal("PostgreSQL at somewhere:1: in use", backing["PostgreSQL"].Describe());
    }

    [Fact]
    public async Task A_service_that_is_down_is_stood_in_for_after_the_wait_and_the_start_goes_on()
    {
        var service = new FakeService("RabbitMQ", true, Down);
        var watch = Stopwatch.StartNew();
        var backing = await BackingServices.DecideAsync([service], TimeSpan.FromSeconds(1.2), default);

        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(0.6), TimeSpan.FromSeconds(3));
        Assert.True(service.Attempts >= 2, "tried again during the wait");
        var choice = Assert.Single(backing.FellBack);
        Assert.Equal("cannot be reached", choice.Problem);
        Assert.Equal("RabbitMQ at somewhere:1 cannot be reached; using its stand-in instead until the service is restarted", choice.Describe());
    }

    [Fact]
    public async Task A_service_that_comes_up_during_the_wait_is_used()
    {
        // Started next to the API, as in a compose file: down for the first two tries.
        var service = new FakeService("Redis", true, attempt => attempt <= 2 ? Down(attempt) : Task.CompletedTask);
        var backing = await BackingServices.DecideAsync([service], TimeSpan.FromSeconds(5), default);

        Assert.True(backing.Uses("Redis"));
        Assert.Equal(3, service.Attempts);
    }

    [Fact]
    public async Task With_no_wait_each_service_is_still_tried_once_and_all_are_tried_together()
    {
        var postgresStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var redisStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = TimeSpan.FromSeconds(30);
        async Task Probe(TaskCompletionSource started)
        {
            started.TrySetResult();
            await release.Task;
            await Down(0);
        }
        var slow = new FakeService("PostgreSQL", true, _ => Probe(postgresStarted));
        var other = new FakeService("Redis", true, _ => Probe(redisStarted));
        var deciding = BackingServices.DecideAsync([slow, other], TimeSpan.Zero, default);

        try
        {
            // Both probes must enter while neither is allowed to finish. The
            // deadline only bounds a hang; elapsed time is not the assertion.
            await Task.WhenAll(postgresStarted.Task, redisStarted.Task).WaitAsync(deadline);
            Assert.False(deciding.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }
        var backing = await deciding.WaitAsync(deadline);

        Assert.Equal((1, 1), (slow.Attempts, other.Attempts));
        Assert.Equal(2, backing.FellBack.Count);
        // A serial implementation might time out its first probe and only
        // then enter the second. That must not count as concurrent probing:
        // both outcomes must come from the released probes, not their timeout.
        Assert.All(backing.FellBack, choice => Assert.Equal("cannot be reached", choice.Problem));
    }

    [Fact]
    public async Task What_went_wrong_is_said_in_our_words_never_in_the_exceptions()
    {
        // A driver's message may carry the address it was given, user and password included.
        var leaky = new FakeService("PostgreSQL", true, _ => throw new InvalidOperationException("could not connect to postgres://admin:hunter2@db:5432"));
        var backing = await BackingServices.DecideAsync([leaky], TimeSpan.Zero, default);

        Assert.Equal("cannot be used (InvalidOperationException)", backing["PostgreSQL"].Problem);
        Assert.Equal("refused the user name or password", BackingServices.Explain(new PostgresException("password authentication failed for hunter2", "FATAL", "FATAL", "28P01")));
        Assert.Equal("cannot be reached", BackingServices.Explain(new NpgsqlException("x", new System.Net.Sockets.SocketException())));
    }

    [Fact]
    public async Task A_service_found_unusable_after_it_answered_is_stood_in_for_too()
    {
        var backing = await BackingServices.DecideAsync([new FakeService("PostgreSQL", true, _ => Task.CompletedTask)], TimeSpan.Zero, default);
        var after = backing.WithFallback("PostgreSQL", "refused: the user may not create the database or its tables");

        Assert.False(after.Uses("PostgreSQL"));
        Assert.Equal("refused: the user may not create the database or its tables", Assert.Single(after.FellBack).Problem);
    }

    [Fact]
    public void The_wait_and_the_switches_are_checked_with_the_other_settings()
    {
        string? Only(string name, string value, string asked) => asked == name ? value : null;
        Assert.Empty(ServiceConfig.Validate(n => Only(BackingServices.WaitVariable, "2.5", n)));
        Assert.Empty(ServiceConfig.Validate(n => Only(BackingServices.WaitVariable, "0", n)));
        Assert.Single(ServiceConfig.Validate(n => Only(BackingServices.WaitVariable, "2,5", n)));
        Assert.Single(ServiceConfig.Validate(n => Only(BackingServices.WaitVariable, "-1", n)));
        Assert.Single(ServiceConfig.Validate(n => Only(BackingServices.RequireVariable, "yes", n)));
        Assert.Empty(ServiceConfig.Validate(n => Only(BackingServices.RequireVariable, "true", n)));
        Assert.Equal(TimeSpan.FromSeconds(5), BackingServices.WaitFrom(_ => null));
        Assert.Equal(TimeSpan.FromSeconds(0.5), BackingServices.WaitFrom(_ => "0.5"));
    }
}

/// <summary>The PostgreSQL address: what it may look like, and what is made of it.</summary>
public sealed class PostgresOptionsTests
{
    [Theory]
    [InlineData("postgres://cerberus:secret@db:5432/cerberus")]
    [InlineData("postgresql://db")]
    [InlineData("postgres://u@db/my_db-2?sslmode=verify-full")]
    public void Good_addresses_pass(string url) => Assert.Null(PostgresOptions.Problem(url));

    [Theory]
    [InlineData("http://db:5432/cerberus")]
    [InlineData("Host=db;Database=cerberus")]
    [InlineData("postgres://db/name;drop")]
    [InlineData("postgres://db/x?sslmode=sometimes")]
    public void Bad_addresses_stop_the_start_with_a_message_naming_the_setting(string url)
    {
        Assert.StartsWith(PostgresOptions.Variable, PostgresOptions.Problem(url));
        Assert.Contains(ServiceConfig.Validate(name => name == PostgresOptions.Variable ? url : null), e => e.StartsWith(PostgresOptions.Variable, StringComparison.Ordinal));
    }

    [Fact]
    public void The_address_becomes_a_connection_and_is_shown_without_who_or_how()
    {
        var options = new PostgresOptions("postgres://ops%40corp:p%3Ass%2Fw0rd@db.internal:6543/audit?sslmode=require");
        var settings = new NpgsqlConnectionStringBuilder(options.ConnectionString());

        Assert.Equal(("db.internal", 6543, "audit", "ops@corp", "p:ss/w0rd", SslMode.Require), (settings.Host, settings.Port, settings.Database, settings.Username, settings.Password, settings.SslMode));
        Assert.Equal("db.internal:6543/audit", options.Where);
        Assert.Equal("cerberus", new NpgsqlConnectionStringBuilder(new PostgresOptions("postgres://u@db").ConnectionString()).Database);
        Assert.Equal("db:5432/cerberus", new PostgresOptions("postgres://u:x@db").Where);
    }
}

/// <summary>The probes against real servers — and against an address where nothing listens.</summary>
public sealed class RealProbeTests(RedisServer redis) : IClassFixture<RedisServer>
{
    private const string Nowhere = "127.0.0.1:1";

    private static async Task<ServiceChoice> Decide(IServiceProbe probe) =>
        (await BackingServices.DecideAsync([probe], TimeSpan.Zero, default)).Choices.Single();

    [Fact]
    public async Task Nothing_listening_is_said_at_once_for_each_kind_of_service()
    {
        var watch = Stopwatch.StartNew();
        var choices = await Task.WhenAll(
            Decide(new PostgresProbe(new PostgresOptions($"postgres://u:never-shown@{Nowhere}/cerberus"))),
            Decide(new RedisProbe(new RedisOptions($"redis://{Nowhere}"))),
            Decide(new RabbitMqProbe(new RabbitMqOptions($"amqp://guest:guest@{Nowhere}/"))));

        Assert.All(choices, c => Assert.Equal("cannot be reached", c.Problem));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed.TotalSeconds:0.0} s");
        Assert.DoesNotContain("never-shown", choices[0].Describe());
    }

    [RedisFact]
    public async Task A_running_redis_is_used()
    {
        var choice = await Decide(new RedisProbe(new RedisOptions(redis.Url)));
        Assert.True(choice.InUse, choice.Problem);
    }

    [PostgresFact]
    public async Task A_running_postgres_is_used_and_a_missing_database_is_made()
    {
        var url = PostgresTestServer.NewDatabase(out var name);
        try
        {
            var choice = await Decide(new PostgresProbe(new PostgresOptions(url)));

            Assert.True(choice.InUse, choice.Problem);
            await using var connection = PostgresTestServer.Open("postgres");
            await using var exists = new NpgsqlCommand($"SELECT count(*) FROM pg_database WHERE datname = '{name}'", connection);
            Assert.Equal(1L, await exists.ExecuteScalarAsync());
        }
        finally
        {
            PostgresTestServer.Drop(PostgresTestServer.Url!, name);
        }
    }

    [PostgresFact]
    public async Task A_postgres_that_refuses_the_user_is_said_so()
    {
        var server = new Uri(PostgresTestServer.Url!);
        var choice = await Decide(new PostgresProbe(new PostgresOptions($"postgres://nobody_{Guid.NewGuid():n}:x@{server.Host}:{server.Port}/cerberus")));

        Assert.False(choice.InUse);
        Assert.Equal("refused the user name or password", choice.Problem);
    }
}

/// <summary>A broker of its own: started only for this test, as RabbitMqTests does.</summary>
public sealed class RabbitProbeTests(RabbitServer rabbit) : IClassFixture<RabbitServer>
{
    [RabbitFact]
    public async Task A_running_broker_is_used()
    {
        var choice = (await BackingServices.DecideAsync([new RabbitMqProbe(new RabbitMqOptions(rabbit.Url))], TimeSpan.Zero, default)).Choices.Single();
        Assert.True(choice.InUse, choice.Problem);
    }
}

/// <summary>The schema on either database: made by two copies at once, brought forward when something is missing.</summary>
public sealed class SchemaOnBothDatabasesTests
{
    private static int ModelTables(CerberusDbContext db) => db.Model.GetEntityTypes().Select(e => e.GetTableName()).Distinct().Count();

    [Fact]
    public async Task Two_copies_creating_a_new_sqlite_database_at_once_both_start()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cerberus-race-{Guid.NewGuid():n}.db");
        try
        {
            // Each copy has its own context, as two processes would; started together, several times over.
            for (var round = 0; round < 5; round++)
            {
                File.Delete(file);
                var options = new DbContextOptionsBuilder<CerberusDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options;
                var start = new Barrier(2);
                await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
                {
                    using var db = new CerberusDbContext(options);
                    start.SignalAndWait();
                    SchemaUpgrader.EnsureUpToDate(db);
                })));
                using var check = new CerberusDbContext(options);
                Assert.Equal(0, await check.MemoryNotes.CountAsync());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [PostgresFact]
    public async Task On_postgres_two_copies_take_turns_and_what_is_missing_is_brought_back()
    {
        var url = PostgresTestServer.NewDatabase(out var name);
        try
        {
            var probe = new PostgresProbe(new PostgresOptions(url));
            await probe.ProbeAsync(default);
            var builder = new DbContextOptionsBuilder<CerberusDbContext>();
            new PostgresOptions(url).Configure(builder);

            var start = new Barrier(3);
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
            {
                using var db = new CerberusDbContext(builder.Options);
                start.SignalAndWait();
                SchemaUpgrader.EnsureUpToDate(db);
            })));

            await using var connection = PostgresTestServer.Open(name);
            async Task<long> Count(string sql)
            {
                await using var command = new NpgsqlCommand(sql, connection);
                return (long)(await command.ExecuteScalarAsync())!;
            }
            using (var db = new CerberusDbContext(builder.Options))
                Assert.Equal(ModelTables(db), await Count("SELECT count(*) FROM pg_tables WHERE schemaname = current_schema()"));

            // An older database: a table, a column and an index missing. They come back; nothing else changes.
            await using (var older = new NpgsqlCommand("""DROP TABLE "Schedules"; ALTER TABLE "MemoryNotes" DROP COLUMN "TargetPath"; DROP INDEX "IX_Users_Email";""", connection))
                await older.ExecuteNonQueryAsync();
            using (var db = new CerberusDbContext(builder.Options)) SchemaUpgrader.EnsureUpToDate(db);

            Assert.Equal(1, await Count("""SELECT count(*) FROM pg_tables WHERE tablename = 'Schedules'"""));
            Assert.Equal(1, await Count("""SELECT count(*) FROM information_schema.columns WHERE table_name = 'MemoryNotes' AND column_name = 'TargetPath'"""));
            Assert.Equal(1, await Count("""SELECT count(*) FROM pg_indexes WHERE indexname = 'IX_Users_Email'"""));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            PostgresTestServer.Drop(PostgresTestServer.Url!, name);
        }
    }
}

/// <summary>The whole API, started with services that are not there — and on a PostgreSQL that is.</summary>
[Collection("api")]
public sealed class LocalFallbackApiTests(ApiHost host)
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> FinishedAsync(HttpClient client, string jobId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}");
            if (job.GetProperty("state").GetString() is not ("Queued" or "Running")) return job;
            Assert.True(DateTime.UtcNow < deadline, "job did not finish in time");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task With_postgres_redis_and_rabbitmq_configured_but_down_the_service_runs_on_its_own()
    {
        using var alone = host.StartVariant(
            (PostgresOptions.Variable, "postgres://cerberus:not-a-real-secret@127.0.0.1:1/cerberus"),
            ("CERBERUS_REDIS_URL", "redis://127.0.0.1:1"),
            ("CERBERUS_RABBITMQ_URL", "amqp://guest:guest@127.0.0.1:1/"),
            (BackingServices.RequireVariable, "false"));

        // Readiness says what stands in for what, without where any of it was meant to be.
        var ready = await alone.CreateClient().GetAsync("/health/ready");
        var body = await ready.Content.ReadAsStringAsync();
        var report = JsonDocument.Parse(body).RootElement;
        Assert.Equal("degraded", report.GetProperty("status").GetString());
        var services = report.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "services").GetProperty("detail").GetString();
        Assert.Contains("PostgreSQL configured but cannot be reached at start-up, so the local SQLite file is used", services);
        Assert.Contains("Redis configured but cannot be reached at start-up", services);
        Assert.Contains("RabbitMQ configured but cannot be reached at start-up, so the queue inside this process is used", services);
        Assert.DoesNotContain("not-a-real-secret", body);
        Assert.DoesNotContain("127.0.0.1", body);
        Assert.DoesNotContain("redis", report.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()));

        // And it works: an account, a memory note kept, a job through the queue inside the process.
        var client = await ApiHost.AuthenticatedClientAsync(alone);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/memory", new { text = "Kept locally." })).StatusCode);
        Assert.Contains("Kept locally.", await client.GetStringAsync("/memory"));
        var submitted = await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false });
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var job = await FinishedAsync(client, (await Json(submitted)).GetProperty("id").GetString()!);
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());
    }

    [PostgresFact]
    public async Task On_postgres_data_is_kept_there_times_stay_utc_and_the_audit_log_cannot_be_changed()
    {
        var url = PostgresTestServer.NewDatabase(out var name);
        try
        {
            using (var onPostgres = host.StartVariant((PostgresOptions.Variable, url), (BackingServices.RequireVariable, "true")))
            {
                var client = await ApiHost.AuthenticatedClientAsync(onPostgres);
                // A NUL, as a scanned binary file or a tool's raw output may hold: PostgreSQL refuses it in text.
                var added = await client.PostAsJsonAsync("/memory", new { text = "Stored in PostgreSQL.\u0000" });
                Assert.Equal(HttpStatusCode.Created, added.StatusCode);
                var submitted = await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false });
                Assert.Equal("Succeeded", (await FinishedAsync(client, (await Json(submitted)).GetProperty("id").GetString()!)).GetProperty("state").GetString());

                // A time comes back as the moment it was, on a machine whatever its time zone.
                var note = (await client.GetFromJsonAsync<JsonElement>("/memory")).EnumerateArray().Single(n => n.GetProperty("text").GetString()!.StartsWith("Stored in PostgreSQL.", StringComparison.Ordinal));
                Assert.InRange(note.GetProperty("createdAt").GetDateTime().ToUniversalTime(), DateTime.UtcNow.AddMinutes(-2), DateTime.UtcNow.AddMinutes(1));
            }

            await using var connection = PostgresTestServer.Open(name);
            await using (var read = new NpgsqlCommand("""SELECT "Text" FROM "MemoryNotes" """, connection))
                Assert.Equal("Stored in PostgreSQL.�", await read.ExecuteScalarAsync());
            await using (var count = new NpgsqlCommand("""SELECT count(*) FROM "AuditEvents" """, connection))
                Assert.True((long)(await count.ExecuteScalarAsync())! > 0);
            foreach (var change in new[] { """UPDATE "AuditEvents" SET "Action" = 'x'""", """DELETE FROM "AuditEvents" """, """TRUNCATE "AuditEvents" """ })
            {
                await using var attempt = new NpgsqlCommand(change, connection);
                var refused = await Assert.ThrowsAsync<PostgresException>(() => attempt.ExecuteNonQueryAsync());
                Assert.Contains("append-only", refused.MessageText);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            PostgresTestServer.Drop(PostgresTestServer.Url!, name);
        }
    }
}
