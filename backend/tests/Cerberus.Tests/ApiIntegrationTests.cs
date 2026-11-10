using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace Cerberus.Tests;

/// <summary>
/// Hosts the real API in-process against a throwaway database and scan root.
/// The API reads its configuration from environment variables at startup, which
/// are process-wide — so there is exactly one host, shared by every test in the
/// collection, and tests isolate themselves by registering their own user.
/// </summary>
public sealed class ApiHost : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cerberus-it-" + Guid.NewGuid().ToString("n"));
    private readonly WebApplicationFactory<Program> _factory;

    public ApiHost()
    {
        var scanRoot = Path.Combine(_root, "scan");
        Directory.CreateDirectory(Path.Combine(scanRoot, "app"));
        Directory.CreateDirectory(Path.Combine(scanRoot, "other"));
        File.WriteAllText(Path.Combine(scanRoot, "app", "main.py"), "import hashlib\nhashlib.md5(b'x')\n");
        File.WriteAllText(Path.Combine(scanRoot, "other", "ok.py"), "print('hello')\n");

        Environment.SetEnvironmentVariable("CERBERUS_DB", Path.Combine(_root, "test.db"));
        Environment.SetEnvironmentVariable("CERBERUS_KEYRING", Path.Combine(_root, "keys"));
        Environment.SetEnvironmentVariable("CERBERUS_SCAN_ROOT", scanRoot);
        Environment.SetEnvironmentVariable("CERBERUS_CATALOG_ROOT", FindCatalogs());

        _factory = new WebApplicationFactory<Program>();
    }

    public HttpClient CreateClient() => _factory.CreateClient();

    /// <summary>Registers a fresh user and returns a client authenticated as them, plus the token.</summary>
    public async Task<(HttpClient Client, string Token)> NewUserAsync()
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register",
            new { email = $"{Guid.NewGuid():n}@test.dev", password = "secret123" });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, token);
    }

    public HubConnection JobsHub(string? token) => new HubConnectionBuilder()
        .WithUrl("http://localhost/hub/jobs", options =>
        {
            // Route the SignalR client through the in-memory test server.
            options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
            if (token is not null) options.AccessTokenProvider = () => Task.FromResult<string?>(token);
        })
        .Build();

    private static string FindCatalogs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "catalogs"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "catalogs");
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiHost>;

[Collection("api")]
public class ApiIntegrationTests(ApiHost host)
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
            if (job.GetProperty("state").GetString() is not ("Queued" or "Running")) return job;
            Assert.True(DateTime.UtcNow < deadline, "job did not finish in time");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Endpoints_require_a_session()
    {
        var anonymous = host.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        foreach (var path in new[] { "/jobs", "/scans", "/compliance", "/engagements", "/audit" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task Scan_job_runs_and_its_updates_are_pushed_to_the_owner_only()
    {
        var (alice, aliceToken) = await host.NewUserAsync();
        var (_, bobToken) = await host.NewUserAsync();

        var aliceEvents = new ConcurrentQueue<JsonElement>();
        var bobEvents = new ConcurrentQueue<JsonElement>();
        var finished = new TaskCompletionSource();

        await using var aliceHub = host.JobsHub(aliceToken);
        await using var bobHub = host.JobsHub(bobToken);
        aliceHub.On<JsonElement>("jobUpdated", job =>
        {
            aliceEvents.Enqueue(job);
            if (job.GetProperty("state").GetString() == "Succeeded") finished.TrySetResult();
        });
        bobHub.On<JsonElement>("jobUpdated", bobEvents.Enqueue);
        await aliceHub.StartAsync();
        await bobHub.StartAsync();

        var submitted = await alice.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false });
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var jobId = (await Json(submitted)).GetProperty("id").GetString()!;

        await finished.Task.WaitAsync(Patience);

        // States arrive in order and never go backwards.
        var states = aliceEvents.Select(e => e.GetProperty("state").GetString()).ToList();
        Assert.Equal(["Queued", "Running", "Succeeded"], states);
        Assert.All(aliceEvents, e => Assert.Equal(jobId, e.GetProperty("id").GetString()));
        Assert.Empty(bobEvents);

        // The pushed result matches what the API reports, and points at a real scan.
        var job = await WaitForJobAsync(alice, jobId);
        var scanId = job.GetProperty("resultId").GetString();
        Assert.Equal(aliceEvents.Last().GetProperty("resultId").GetString(), scanId);

        var scan = await alice.GetFromJsonAsync<JsonElement>($"/scans/{scanId}");
        Assert.Equal(1, scan.GetProperty("findingCount").GetInt32());   // the MD5 use in app/main.py
    }

    [Fact]
    public async Task Hub_drops_connections_without_a_valid_session()
    {
        foreach (var token in new[] { null, "not-a-real-token" })
        {
            var closed = new TaskCompletionSource();
            await using var hub = host.JobsHub(token);
            hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };

            try
            {
                await hub.StartAsync();
                await closed.Task.WaitAsync(Patience);   // the server aborts it right after connect
            }
            catch (Exception ex) when (ex is not TimeoutException)
            {
                // Refused during the handshake — equally acceptable.
            }
            Assert.NotEqual(HubConnectionState.Connected, hub.State);
        }
    }

    [Fact]
    public async Task Assessment_job_from_a_scan_produces_a_stored_assessment()
    {
        var (client, _) = await host.NewUserAsync();
        var scan = await Json(await client.PostAsJsonAsync("/scan", new { path = "app", sca = false }));
        var scanId = scan.GetProperty("id").GetString();

        var submitted = await client.PostAsJsonAsync("/jobs", new { type = "assess", catalogId = "owasp-top10", scanId });
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var job = await WaitForJobAsync(client, (await Json(submitted)).GetProperty("id").GetString()!);

        Assert.Equal("Succeeded", job.GetProperty("state").GetString());
        Assert.Equal("app", job.GetProperty("targetPath").GetString());

        var assessment = await client.GetFromJsonAsync<JsonElement>($"/compliance/{job.GetProperty("resultId").GetString()}");
        Assert.Equal("owasp-top10", assessment.GetProperty("catalogId").GetString());
        Assert.Equal(scanId, assessment.GetProperty("scanId").GetString());
        var a02 = assessment.GetProperty("controls").EnumerateArray().Single(c => c.GetProperty("controlId").GetString() == "A02");
        Assert.Equal("NonCompliant", a02.GetProperty("verdict").GetString());   // weak hash → cryptographic failures
    }

    [Fact]
    public async Task Job_submission_is_validated()
    {
        var (client, _) = await host.NewUserAsync();

        async Task<HttpStatusCode> Submit(object body) => (await client.PostAsJsonAsync("/jobs", body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Submit(new { type = "exploit", path = "app" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Submit(new { type = "scan", path = "../outside" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Submit(new { type = "assess", catalogId = "nope", path = "app" }));
        Assert.Equal(HttpStatusCode.NotFound, await Submit(new { type = "assess", scanId = "missing" }));
        Assert.Equal(HttpStatusCode.NotFound, await Submit(new { type = "scan", path = "app", engagementId = "missing" }));
    }

    [Fact]
    public async Task Engagement_scope_is_enforced_for_jobs_and_abort_blocks_further_work()
    {
        var (client, _) = await host.NewUserAsync();
        var engagement = await Json(await client.PostAsJsonAsync("/engagements", new { name = "Review" }));
        var id = engagement.GetProperty("id").GetString();
        var target = await client.PostAsJsonAsync($"/engagements/{id}/targets", new { locator = "app", ownershipAttested = true });
        Assert.Equal(HttpStatusCode.Created, target.StatusCode);

        // Out of scope: refused before anything is queued.
        var outside = await client.PostAsJsonAsync("/jobs", new { path = "other", sca = false, engagementId = id });
        Assert.Equal(HttpStatusCode.Forbidden, outside.StatusCode);

        // In scope: runs, and the scan carries the engagement.
        var inside = await client.PostAsJsonAsync("/jobs", new { path = "app", sca = false, engagementId = id });
        var job = await WaitForJobAsync(client, (await Json(inside)).GetProperty("id").GetString()!);
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());
        var scan = await client.GetFromJsonAsync<JsonElement>($"/scans/{job.GetProperty("resultId").GetString()}");
        Assert.Equal("app", scan.GetProperty("targetPath").GetString());

        // After the kill switch nothing more is accepted.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/engagements/{id}/abort", null)).StatusCode);
        var after = await client.PostAsJsonAsync("/jobs", new { path = "app", sca = false, engagementId = id });
        Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);

        // Every decision is in the audit log, and the chain verifies.
        var audit = await client.GetFromJsonAsync<JsonElement>($"/audit?engagementId={id}");
        var entries = audit.EnumerateArray()
            .Select(e => $"{e.GetProperty("action").GetString()}:{e.GetProperty("outcome").GetString()}").ToList();
        Assert.Equal(2, entries.Count(e => e == "job.submit:denied"));
        Assert.Contains("job.submit:allowed", entries);
        Assert.Contains("job.start:allowed", entries);
        Assert.Contains("engagement.abort:ok", entries);

        var verify = await client.GetFromJsonAsync<JsonElement>("/audit/verify");
        Assert.True(verify.GetProperty("intact").GetBoolean());
    }

    [Fact]
    public async Task Users_cannot_see_each_others_jobs()
    {
        var (alice, _) = await host.NewUserAsync();
        var (bob, _) = await host.NewUserAsync();
        var submitted = await Json(await alice.PostAsJsonAsync("/jobs", new { path = "app", sca = false }));
        var jobId = submitted.GetProperty("id").GetString()!;
        await WaitForJobAsync(alice, jobId);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/jobs/{jobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/jobs/{jobId}/cancel", null)).StatusCode);
        Assert.Equal(0, (await bob.GetFromJsonAsync<JsonElement>("/jobs")).GetArrayLength());
    }
}
