using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api.Auth;
using Cerberus.Data;
using Cerberus.Scanning;
using Microsoft.Extensions.DependencyInjection;

namespace Cerberus.Tests;

public class SecretMaskerTests
{
    private const string Key = "AKIAIOSFODNN7EXAMPLE";

    [Fact]
    public void A_credential_is_replaced_by_its_length_and_the_code_around_it_stays()
    {
        var masked = SecretMasker.Mask($"aws_key = \"{Key}\"  # prod");

        Assert.DoesNotContain(Key, masked);
        Assert.Contains("[REDACTED len=20]", masked);
        Assert.StartsWith("aws_key = ", masked);
        Assert.EndsWith("# prod", masked);
    }

    [Fact]
    public void Code_without_credentials_is_left_alone_and_a_block_is_masked_line_by_line()
    {
        Assert.Equal("os.system(cmd)", SecretMasker.Mask("os.system(cmd)"));

        var block = SecretMasker.MaskText($"import os\nKEY = \"{Key}\"\nprint(1)");

        Assert.Equal(3, block.Split('\n').Length);
        Assert.DoesNotContain(Key, block);
        Assert.StartsWith("import os\n", block);
        Assert.EndsWith("\nprint(1)", block);
    }
}

public class LoginThrottleTests
{
    private static (LoginThrottle Throttle, Action<TimeSpan> Advance) Build()
    {
        var now = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        return (new LoginThrottle(() => now), by => now += by);
    }

    [Fact]
    public void Five_failures_in_a_row_lock_the_account_for_a_while()
    {
        var (throttle, advance) = Build();

        for (var i = 0; i < 4; i++) Assert.False(throttle.Failed("Me@Example.dev"));
        Assert.Null(throttle.LockedFor("me@example.dev"));
        Assert.True(throttle.Failed("me@example.dev"));              // the fifth locks; the name is not case-sensitive

        Assert.Equal(LoginThrottle.Lock, throttle.LockedFor("me@example.dev"));
        Assert.Null(throttle.LockedFor("other@example.dev"));       // per account

        advance(LoginThrottle.Lock - TimeSpan.FromSeconds(1));
        Assert.NotNull(throttle.LockedFor("me@example.dev"));
        advance(TimeSpan.FromSeconds(2));
        Assert.Null(throttle.LockedFor("me@example.dev"));
        Assert.False(throttle.Failed("me@example.dev"));             // and the count starts again
    }

    [Fact]
    public void A_success_or_a_long_pause_resets_the_count()
    {
        var (throttle, advance) = Build();

        for (var i = 0; i < 4; i++) throttle.Failed("a@x.dev");
        throttle.Succeeded("a@x.dev");
        for (var i = 0; i < 4; i++) Assert.False(throttle.Failed("a@x.dev"));

        advance(LoginThrottle.Window + TimeSpan.FromSeconds(1));
        Assert.False(throttle.Failed("a@x.dev"));                    // old failures no longer count
        Assert.Null(throttle.LockedFor("a@x.dev"));
    }
}

[Collection("api")]
public class HardeningApiTests(ApiHost host)
{
    private const string Key = "AKIAIOSFODNN7EXAMPLE";

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

    /// <summary>A directory under the scan root holding one file with a credential in it.</summary>
    private static string PlantSecret()
    {
        var name = "secret-" + Guid.NewGuid().ToString("n")[..8];
        var dir = Path.Combine(Environment.GetEnvironmentVariable("CERBERUS_SCAN_ROOT")!, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "settings.py"), $"import os\naws_key = \"{Key}\"\nos.system(cmd)\n");
        return name;
    }

    [Fact]
    public async Task A_found_credential_is_not_kept_or_returned_anywhere_only_where_it_is()
    {
        var (client, _) = await host.NewUserAsync();
        var path = PlantSecret();

        // Found by the scan, and by the job's event stream…
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path, sca = false }));
        var job = await WaitForEndAsync(client, submitted.GetProperty("id").GetString()!);
        var scanId = job.GetProperty("resultId").GetString();
        var scan = await client.GetStringAsync($"/scans/{scanId}");
        var events = await client.GetStringAsync($"/jobs/{job.GetProperty("id").GetString()}/events?after=0");
        Assert.Contains("secret.aws-access-key", scan);
        Assert.Contains($"{path}/settings.py", scan);

        // …and a compliance assessment built on it, and an agent that reads the very file (a local model sees it; the record does not).
        var assessment = await Json(await client.PostAsJsonAsync("/compliance/assess", new { catalogId = "owasp-top10", scanId }));
        var matrix = await client.GetStringAsync($"/compliance/{assessment.GetProperty("id").GetString()}");
        var agent = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path, model = "llama3.1" }));
        var agentJob = await WaitForEndAsync(client, agent.GetProperty("id").GetString()!);
        var agentEvents = await client.GetStringAsync($"/jobs/{agentJob.GetProperty("id").GetString()}/events?after=0");
        var agentRun = await client.GetStringAsync($"/agent-runs/{agentJob.GetProperty("resultId").GetString()}");
        Assert.Contains("readFile", agentEvents);
        Assert.Contains("os.system(cmd)", agentEvents);   // the file was read and shown — masked

        foreach (var text in new[] { scan, events, matrix, agentEvents, agentRun })
        {
            Assert.DoesNotContain(Key, text);
        }
        Assert.Contains("[REDACTED len=20]", scan);

        // Nor in the database.
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        Assert.DoesNotContain(Key, db.Scans.Single(s => s.Id == scanId).Findings);
        Assert.DoesNotContain(db.JobEvents.Where(e => e.JobId == agentJob.GetProperty("id").GetString()).Select(e => e.Data).ToList(), data => data.Contains(Key));
    }

    [Fact]
    public async Task A_scan_stored_before_evidence_was_masked_is_masked_on_the_way_out()
    {
        var (client, token) = await host.NewUserAsync();
        string scanId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
            var scan = new Scan
            {
                UserId = db.AuthSessions.Single(a => a.Token == token).UserId, TargetPath = "old",
                Findings = JsonSerializer.Serialize(new[] { new { id = "secret.aws-access-key:old/a.py:1", title = "AWS key", evidence = $"k = \"{Key}\"" } }),
            };
            db.Scans.Add(scan);
            db.SaveChanges();
            scanId = scan.Id;
        }

        var returned = await client.GetStringAsync($"/scans/{scanId}");

        Assert.DoesNotContain(Key, returned);
        Assert.Contains("[REDACTED len=20]", returned);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_sign_in_for_that_account_only()
    {
        var anonymous = host.CreateClient();
        var email = $"{Guid.NewGuid():n}@test.dev";
        var otherEmail = $"{Guid.NewGuid():n}@test.dev";
        await anonymous.PostAsJsonAsync("/auth/register", new { email, password = "right-password" });
        await anonymous.PostAsJsonAsync("/auth/register", new { email = otherEmail, password = "right-password" });

        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/auth/login", new { email, password = "wrong-guess" })).StatusCode);
        }

        // Locked: even the right password is refused, with a hint when to retry.
        var locked = await anonymous.PostAsJsonAsync("/auth/login", new { email, password = "right-password" });
        Assert.Equal((HttpStatusCode)429, locked.StatusCode);
        Assert.True(locked.Headers.RetryAfter!.Delta > TimeSpan.Zero);
        Assert.Contains("Too many failed sign-ins", (await Json(locked)).GetProperty("error").GetString());

        // Another account is unaffected, and the owner can see what happened once they are in again.
        var other = await anonymous.PostAsJsonAsync("/auth/login", new { email = otherEmail, password = "right-password" });
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);

        host.Services.GetRequiredService<LoginThrottle>().Succeeded(email);   // as if the lock had run out
        var back = await Json(await anonymous.PostAsJsonAsync("/auth/login", new { email, password = "right-password" }));
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", back.GetProperty("token").GetString());
        var lockout = (await client.GetFromJsonAsync<JsonElement>("/audit?limit=20")).EnumerateArray()
            .Single(e => e.GetProperty("action").GetString() == "auth.lockout");
        Assert.Equal("denied", lockout.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_few_wrong_passwords_then_the_right_one_signs_in_and_clears_the_count()
    {
        var anonymous = host.CreateClient();
        var email = $"{Guid.NewGuid():n}@test.dev";
        await anonymous.PostAsJsonAsync("/auth/register", new { email, password = "right-password" });

        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++)
            {
                await anonymous.PostAsJsonAsync("/auth/login", new { email, password = "wrong-guess" });
            }
            Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/auth/login", new { email, password = "right-password" })).StatusCode);
        }
    }
}
