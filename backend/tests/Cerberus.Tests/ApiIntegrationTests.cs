using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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
        Directory.CreateDirectory(Path.Combine(scanRoot, "svc"));
        File.WriteAllText(Path.Combine(scanRoot, "svc", "loader.py"),
            "import pickle\n" +
            "obj = pickle.loads(blob)\n" +
            "legacy = hashlib.md5(x)  # cerberus:ignore crypto.weak-hash\n");

        Environment.SetEnvironmentVariable("CERBERUS_DB", Path.Combine(_root, "test.db"));
        Environment.SetEnvironmentVariable("CERBERUS_KEYRING", Path.Combine(_root, "keys"));
        Environment.SetEnvironmentVariable("CERBERUS_SCAN_ROOT", scanRoot);
        Environment.SetEnvironmentVariable("CERBERUS_CATALOG_ROOT", FindCatalogs());

        _factory = NewFactory();
    }

    /// <summary>The model behind the "echo" model id: calls a deserialization finding real, anything else harmless.</summary>
    public ScriptedProvider Model { get; } = new(Cerberus.Core.Models.ModelProvider.Mock, request =>
        request.Messages[^1].Content.Contains("deserialization.unsafe")
            ? """{"verdict":"true_positive","reasoning":"pickle.loads runs on data from outside.","remediation":"Use JSON."}"""
            : """{"verdict":"false_positive","reasoning":"Not reachable.","remediation":""}""");

    /// <summary>Behind the Ollama model ids in tests: follows the dev model's rules, so it can call tools.</summary>
    public RuleFollowingProvider Tools { get; } = new(Cerberus.Core.Models.ModelProvider.Ollama);

    /// <summary>Stands in for the Anthropic API; records every request that would have left the machine.</summary>
    public ScriptedProvider Cloud { get; } = new(Cerberus.Core.Models.ModelProvider.Anthropic,
        _ => """{"verdict":"true_positive","reasoning":"Reviewed remotely.","remediation":"Fix it."}""");

    /// <summary>Stands in for the DeepSeek API; records every request that would have left the machine.</summary>
    public ScriptedProvider DeepSeek { get; } = new(Cerberus.Core.Models.ModelProvider.DeepSeek, _ => "Answered by the DeepSeek stand-in.");

    /// <summary>Stands in for the local model server's list of installed models; null means it does not answer.</summary>
    public FakeLocalModels LocalModels { get; } = new();

    /// <summary>Stands in for the embedding model; records what it was asked to embed.</summary>
    public WordEmbedder Embedder { get; } = new();

    /// <summary>Stands in for DNS and HTTP so ownership verification is testable offline.</summary>
    public FakeOwnershipProbe Probe { get; } = new();

    private WebApplicationFactory<Program> NewFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<Cerberus.Authorization.IOwnershipProbe>(Probe);

                // No embedding model is run by the tests: vectors come from the words of the text.
                services.Remove(services.Single(d => d.ServiceType == typeof(Cerberus.Api.Search.IEmbedder)));
                services.AddSingleton<Cerberus.Api.Search.IEmbedder>(Embedder);

                // Retries happen as in production, without the waiting.
                services.AddSingleton(new Cerberus.Models.RetryPolicy(
                    5, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), (_, _) => Task.CompletedTask));

                // Swap the built-in "echo" model for one that answers triage requests from a script.
                var echo = services.Single(d => d.ImplementationType == typeof(Cerberus.Models.MockProvider));
                services.Remove(echo);
                services.AddSingleton<Cerberus.Core.Models.IModelProvider>(Model);

                // No test may ever reach a real model provider: the cloud providers are scripted too.
                foreach (var real in services.Where(d =>
                             d.ImplementationType == typeof(Cerberus.Models.AnthropicProvider) ||
                             d.ImplementationType == typeof(Cerberus.Models.OpenAiProvider) ||
                             d.ImplementationType == typeof(Cerberus.Models.DeepSeekProvider) ||
                             d.ImplementationType == typeof(Cerberus.Models.OllamaProvider)).ToList())
                {
                    services.Remove(real);
                }
                services.AddSingleton<Cerberus.Core.Models.IModelProvider>(Cloud);
                services.AddSingleton<Cerberus.Core.Models.IModelProvider>(DeepSeek);

                // Which local models are installed is scripted too: no test asks a model server.
                services.Remove(services.Single(d => d.ServiceType == typeof(Cerberus.Api.ILocalModelCatalog)));
                services.AddSingleton<Cerberus.Api.ILocalModelCatalog>(LocalModels);
                services.AddSingleton<Cerberus.Core.Models.IModelProvider>(
                    new ScriptedProvider(Cerberus.Core.Models.ModelProvider.OpenAI, _ => throw new InvalidOperationException("not used")));
                // Ollama model ids are served by a rule-following stand-in that can call tools
                // (the same rules as the "echo" dev model). No local model is ever run.
                services.AddSingleton<Cerberus.Core.Models.IModelProvider>(Tools);
            }));

    /// <summary>
    /// A second API instance over the same database, started with one extra
    /// environment setting. The setting is only read at startup, so it is
    /// restored as soon as the instance is up.
    /// </summary>
    public WebApplicationFactory<Program> StartVariant(string variable, string value)
    {
        Environment.SetEnvironmentVariable(variable, value);
        try
        {
            var factory = NewFactory();
            factory.CreateClient().Dispose();   // forces startup while the setting is in place
            return factory;
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    public static async Task<HttpClient> AuthenticatedClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/auth/register",
            new { email = $"{Guid.NewGuid():n}@test.dev", password = "secret123" });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient CreateClient() => _factory.CreateClient();

    /// <summary>The running API's services, for tests that need to build one of its parts directly.</summary>
    public IServiceProvider Services => _factory.Services;

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

    public HubConnection JobsHub(string? token) => Hub("/hub/jobs", token);

    public HubConnection Hub(string path, string? token) => new HubConnectionBuilder()
        .WithUrl($"http://localhost{path}", options =>
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

public sealed class FakeLocalModels : Cerberus.Api.ILocalModelCatalog
{
    public IReadOnlyList<string>? Installed { get; set; } = ["llama3.1:latest", "nomic-chat:1b", "qwen2.5-coder:7b"];

    public Task<IReadOnlyList<string>?> InstalledAsync(CancellationToken ct) => Task.FromResult(Installed);
}

public sealed class FakeOwnershipProbe : Cerberus.Authorization.IOwnershipProbe
{
    public ConcurrentDictionary<string, string> Txt { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, string> Pages { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(Txt.TryGetValue(name, out var value) ? [value] : []);

    public Task<string?> FetchAsync(Uri url, CancellationToken ct) =>
        Task.FromResult(Pages.TryGetValue(url.ToString(), out var body) ? body : null);
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

    [Fact]
    public async Task Domain_target_is_verified_only_after_the_challenge_is_published()
    {
        var (client, _) = await host.NewUserAsync();
        var engagement = await Json(await client.PostAsJsonAsync("/engagements", new { name = "Live" }));
        var id = engagement.GetProperty("id").GetString();
        var domain = $"{Guid.NewGuid():n}.example";
        var target = await Json(await client.PostAsJsonAsync($"/engagements/{id}/targets", new { type = "domain", locator = domain }));
        var targetId = target.GetProperty("id").GetString();
        Assert.Equal("Pending", target.GetProperty("verification").GetString());

        var challenge = await client.GetFromJsonAsync<JsonElement>($"/engagements/{id}/targets/{targetId}/challenge");
        var recordName = challenge.GetProperty("dns").GetProperty("name").GetString()!;
        var value = challenge.GetProperty("value").GetString()!;
        Assert.Equal($"_cerberus-challenge.{domain}", recordName);
        Assert.StartsWith("cerberus-verify=", value);

        // Nothing published yet, then the wrong value: both refused, target stays Pending.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync($"/engagements/{id}/targets/{targetId}/verify", null)).StatusCode);
        host.Probe.Txt[recordName] = "cerberus-verify=someone-elses-token";
        var wrong = await client.PostAsync($"/engagements/{id}/targets/{targetId}/verify", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrong.StatusCode);
        Assert.False((await Json(wrong)).GetProperty("verified").GetBoolean());

        host.Probe.Txt[recordName] = value;
        var verified = await Json(await client.PostAsync($"/engagements/{id}/targets/{targetId}/verify", null));
        Assert.True(verified.GetProperty("verified").GetBoolean());
        Assert.Equal("Dns", verified.GetProperty("method").GetString());

        var reloaded = await client.GetFromJsonAsync<JsonElement>($"/engagements/{id}");
        var stored = reloaded.GetProperty("targets").EnumerateArray().Single();
        Assert.Equal("Verified", stored.GetProperty("verification").GetString());
        Assert.Equal("Dns", stored.GetProperty("verificationMethod").GetString());

        // Attempts are audited: two refusals, then the success.
        var audit = await client.GetFromJsonAsync<JsonElement>($"/audit?engagementId={id}");
        var outcomes = audit.EnumerateArray().Where(e => e.GetProperty("action").GetString() == "target.verify")
            .Select(e => e.GetProperty("outcome").GetString()).Reverse().ToList();
        Assert.Equal(["denied", "denied", "ok"], outcomes);
    }

    [Fact]
    public async Task Another_users_target_cannot_be_challenged_or_verified()
    {
        var (alice, _) = await host.NewUserAsync();
        var (bob, _) = await host.NewUserAsync();
        var id = (await Json(await alice.PostAsJsonAsync("/engagements", new { name = "Mine" }))).GetProperty("id").GetString();
        var targetId = (await Json(await alice.PostAsJsonAsync($"/engagements/{id}/targets", new { type = "host", locator = "web-01.example" })))
            .GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/engagements/{id}/targets/{targetId}/challenge")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/engagements/{id}/targets/{targetId}/verify", null)).StatusCode);
    }

    [Fact]
    public async Task Path_targets_have_no_challenge()
    {
        var (client, _) = await host.NewUserAsync();
        var id = (await Json(await client.PostAsJsonAsync("/engagements", new { name = "Code" }))).GetProperty("id").GetString();
        var targetId = (await Json(await client.PostAsJsonAsync($"/engagements/{id}/targets", new { locator = "app", ownershipAttested = true })))
            .GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/engagements/{id}/targets/{targetId}/challenge")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/engagements/{id}/targets/{targetId}/verify", null)).StatusCode);
    }

    [Fact]
    public async Task Audit_log_can_be_exported_and_reverified_offline()
    {
        var (client, _) = await host.NewUserAsync();
        await client.PostAsJsonAsync("/engagements", new { name = "=HYPERLINK(\"x\"), with comma" });
        await client.PostAsJsonAsync("/scan", new { path = "app", sca = false });

        var response = await client.GetAsync("/audit/export");
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal([1L, 2L], lines.Select(l => l.GetProperty("sequence").GetInt64()).ToArray());
        // Each line links to the one before it, so the file can be checked without the server.
        Assert.Equal(lines[0].GetProperty("hash").GetString(), lines[1].GetProperty("previousHash").GetString());

        var csv = await client.GetStringAsync("/audit/export?format=csv");
        var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, rows.Length);
        Assert.StartsWith("sequence,createdAt,action", rows[0]);
        Assert.Contains("\"Created engagement \"\"=HYPERLINK(\"\"x\"\"), with comma\"\" (max tier T0).\"", rows[1]);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/audit/export?format=xml")).StatusCode);
    }

    [Fact]
    public async Task When_an_engagement_is_mandatory_runs_without_one_are_refused()
    {
        using var strict = host.StartVariant("CERBERUS_REQUIRE_ENGAGEMENT", "true");
        var client = await ApiHost.AuthenticatedClientAsync(strict);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/scan", new { path = "app", sca = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { path = "app", sca = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/compliance/assess", new { path = "app", sca = false })).StatusCode);

        var id = (await Json(await client.PostAsJsonAsync("/engagements", new { name = "Required" }))).GetProperty("id").GetString();
        await client.PostAsJsonAsync($"/engagements/{id}/targets", new { locator = "app", ownershipAttested = true });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/scan", new { path = "app", sca = false, engagementId = id })).StatusCode);
    }

    [Fact]
    public async Task Suppressed_findings_are_returned_but_do_not_count_or_fail_controls()
    {
        var (client, _) = await host.NewUserAsync();

        var scan = await Json(await client.PostAsJsonAsync("/scan", new { path = "svc", sca = false }));
        Assert.Equal(1, scan.GetProperty("findingCount").GetInt32());
        Assert.Equal(1, scan.GetProperty("suppressedCount").GetInt32());
        Assert.False(scan.GetProperty("truncated").GetBoolean());

        var findings = scan.GetProperty("findings").EnumerateArray().ToList();
        Assert.Equal(2, findings.Count);
        Assert.Equal("deserialization", findings[0].GetProperty("category").GetString());
        Assert.Equal("A08", findings[0].GetProperty("owasp").GetString());
        Assert.False(string.IsNullOrEmpty(findings[0].GetProperty("remediation").GetString()));
        Assert.True(findings[1].GetProperty("suppressed").GetBoolean());

        var assessment = await Json(await client.PostAsJsonAsync("/compliance/assess",
            new { catalogId = "owasp-top10", scanId = scan.GetProperty("id").GetString() }));
        string Verdict(string id) => assessment.GetProperty("controls").EnumerateArray()
            .Single(c => c.GetProperty("controlId").GetString() == id).GetProperty("verdict").GetString()!;

        Assert.Equal("NonCompliant", Verdict("A08"));   // unsafe deserialization
        Assert.Equal("Compliant", Verdict("A02"));      // the only weak-hash hit was suppressed by the target
    }

    [Fact]
    public async Task Triage_job_annotates_findings_without_changing_them_or_compliance()
    {
        var (client, _) = await host.NewUserAsync();
        var scan = await Json(await client.PostAsJsonAsync("/scan", new { path = "svc", sca = false }));
        var scanId = scan.GetProperty("id").GetString();

        // The model must be named: no default decides where code is sent.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "triage", scanId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "triage", scanId, model = "no-such-model" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "triage", model = "echo" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/jobs", new { type = "triage", scanId = "missing", model = "echo" })).StatusCode);

        var submitted = await client.PostAsJsonAsync("/jobs", new { type = "triage", scanId, model = "echo" });
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var job = await WaitForJobAsync(client, (await Json(submitted)).GetProperty("id").GetString()!);
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());
        Assert.Equal(scanId, job.GetProperty("resultId").GetString());

        var stored = await client.GetFromJsonAsync<JsonElement>($"/scans/{scanId}");
        // Only the active finding was sent; the one the target suppressed was not.
        var opinion = stored.GetProperty("triage").EnumerateArray().Single();
        Assert.Equal("TruePositive", opinion.GetProperty("verdict").GetString());
        Assert.Equal("echo", opinion.GetProperty("model").GetString());
        Assert.StartsWith("deserialization.unsafe:svc/loader.py:", opinion.GetProperty("findingId").GetString());
        Assert.Equal(2, stored.GetProperty("findings").GetArrayLength());
        Assert.Equal(1, stored.GetProperty("findingCount").GetInt32());

        var audit = await client.GetFromJsonAsync<JsonElement>("/audit");
        var entry = audit.EnumerateArray().Single(e => e.GetProperty("action").GetString() == "triage.run");
        Assert.Contains("1 of 1 finding(s) reviewed by echo", entry.GetProperty("detail").GetString());
        Assert.Contains("a local model", entry.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_false_positive_opinion_does_not_clear_a_compliance_control()
    {
        var (client, _) = await host.NewUserAsync();
        var scan = await Json(await client.PostAsJsonAsync("/scan", new { path = "app", sca = false }));
        var scanId = scan.GetProperty("id").GetString();

        var submitted = await client.PostAsJsonAsync("/jobs", new { type = "triage", scanId, model = "echo" });
        await WaitForJobAsync(client, (await Json(submitted)).GetProperty("id").GetString()!);
        var stored = await client.GetFromJsonAsync<JsonElement>($"/scans/{scanId}");
        Assert.Equal("FalsePositive", stored.GetProperty("triage").EnumerateArray().Single().GetProperty("verdict").GetString());

        // The model said "harmless"; the control still fails on the scanner's finding.
        var assessment = await Json(await client.PostAsJsonAsync("/compliance/assess", new { catalogId = "owasp-top10", scanId }));
        var a02 = assessment.GetProperty("controls").EnumerateArray().Single(c => c.GetProperty("controlId").GetString() == "A02");
        Assert.Equal("NonCompliant", a02.GetProperty("verdict").GetString());
    }

    private const string CloudModel = "claude-haiku-4-5";

    private async Task<(JsonElement Job, JsonElement Approval, string ScanId)> SubmitCloudTriageAsync(
        HttpClient client, string? engagementId = null)
    {
        var scan = await Json(await client.PostAsJsonAsync("/scan", new { path = "svc", sca = false, engagementId }));
        var scanId = scan.GetProperty("id").GetString()!;
        var submitted = await client.PostAsJsonAsync("/jobs", new { type = "triage", scanId, model = CloudModel });
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var body = await Json(submitted);
        return (body.GetProperty("job"), body.GetProperty("approval"), scanId);
    }

    [Fact]
    public async Task Sending_code_to_a_cloud_model_waits_for_the_operator_and_runs_only_after_approval()
    {
        var (client, token) = await host.NewUserAsync();
        var events = new ConcurrentQueue<(string Name, JsonElement Body)>();
        await using var hub = host.JobsHub(token);
        foreach (var name in new[] { "jobUpdated", "approvalRequested", "approvalResolved" })
        {
            hub.On<JsonElement>(name, body => events.Enqueue((name, body)));
        }
        await hub.StartAsync();
        var sentBefore = host.Cloud.Requests.Count;

        var (job, approval, scanId) = await SubmitCloudTriageAsync(client);
        var jobId = job.GetProperty("id").GetString()!;
        var approvalId = approval.GetProperty("id").GetString()!;

        // The job is parked and the request says exactly what would leave the machine.
        Assert.Equal("AwaitingApproval", job.GetProperty("state").GetString());
        Assert.Equal(approvalId, job.GetProperty("approvalId").GetString());
        Assert.Equal("Pending", approval.GetProperty("status").GetString());
        Assert.Equal("send-code-to-cloud-model", approval.GetProperty("kind").GetString());
        var detail = approval.GetProperty("detail").GetString()!;
        Assert.Contains("1 finding(s) in 1 file(s)", detail);
        Assert.Contains("Anthropic", detail);
        Assert.Contains(CloudModel, detail);

        // It stays parked: nothing runs and nothing is sent while the operator has not decided.
        await Task.Delay(300);
        Assert.Equal("AwaitingApproval", (await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}")).GetProperty("state").GetString());
        Assert.Equal(sentBefore, host.Cloud.Requests.Count);
        var pending = await client.GetFromJsonAsync<JsonElement>("/approvals?status=pending");
        Assert.Equal(approvalId, pending.EnumerateArray().Single().GetProperty("id").GetString());

        var decided = await client.PostAsJsonAsync($"/approvals/{approvalId}/approve", new { note = "ok for this scan" });
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        Assert.Equal("Approved", (await Json(decided)).GetProperty("status").GetString());

        var finished = await WaitForJobAsync(client, jobId);
        Assert.Equal("Succeeded", finished.GetProperty("state").GetString());
        Assert.Equal(sentBefore + 1, host.Cloud.Requests.Count);
        var stored = await client.GetFromJsonAsync<JsonElement>($"/scans/{scanId}");
        Assert.Equal(CloudModel, stored.GetProperty("triage").EnumerateArray().Single().GetProperty("model").GetString());

        // A decision is final.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/approvals/{approvalId}/deny", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/approvals/{approvalId}/approve", null)).StatusCode);

        // The operator was told live, in order: asked, then answered, then the job ran.
        var deadline = DateTime.UtcNow + Patience;
        while (!events.Any(e => e.Name == "jobUpdated" && e.Body.GetProperty("state").GetString() == "Succeeded"))
        {
            Assert.True(DateTime.UtcNow < deadline, "final job update was not pushed");
            await Task.Delay(25);
        }
        var names = events.Select(e => e.Name == "jobUpdated" ? $"job:{e.Body.GetProperty("state").GetString()}" : e.Name).ToList();
        Assert.Equal(
            ["job:AwaitingApproval", "approvalRequested", "approvalResolved", "job:Queued", "job:Running", "job:Succeeded"],
            names);

        var audit = await client.GetFromJsonAsync<JsonElement>("/audit");
        var actions = audit.EnumerateArray().Select(e => e.GetProperty("action").GetString()).Reverse().ToList();
        Assert.True(actions.IndexOf("approval.request") < actions.IndexOf("approval.approve"));
        Assert.True(actions.IndexOf("approval.approve") < actions.IndexOf("triage.run"));
    }

    [Fact]
    public async Task Denying_ends_the_job_without_sending_anything()
    {
        var (client, _) = await host.NewUserAsync();
        var sentBefore = host.Cloud.Requests.Count;
        var (job, approval, scanId) = await SubmitCloudTriageAsync(client);
        var jobId = job.GetProperty("id").GetString()!;

        var denied = await Json(await client.PostAsJsonAsync(
            $"/approvals/{approval.GetProperty("id").GetString()}/deny", new { note = "customer code must stay local" }));
        Assert.Equal("Denied", denied.GetProperty("status").GetString());

        var finished = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}");
        Assert.Equal("Cancelled", finished.GetProperty("state").GetString());
        Assert.Contains("customer code must stay local", finished.GetProperty("error").GetString());

        await Task.Delay(300);
        Assert.Equal(sentBefore, host.Cloud.Requests.Count);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>($"/scans/{scanId}")).GetProperty("triage").GetArrayLength());
    }

    [Fact]
    public async Task Cancelling_a_waiting_job_withdraws_its_request()
    {
        var (client, _) = await host.NewUserAsync();
        var (job, approval, _) = await SubmitCloudTriageAsync(client);
        var approvalId = approval.GetProperty("id").GetString();

        var cancelled = await client.PostAsync($"/jobs/{job.GetProperty("id").GetString()}/cancel", null);
        Assert.Equal("Cancelled", (await Json(cancelled)).GetProperty("state").GetString());

        var withdrawn = await client.GetFromJsonAsync<JsonElement>($"/approvals/{approvalId}");
        Assert.Equal("Cancelled", withdrawn.GetProperty("status").GetString());
        // Too late to approve work that is gone.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/approvals/{approvalId}/approve", null)).StatusCode);
    }

    [Fact]
    public async Task Aborting_an_engagement_withdraws_its_waiting_jobs()
    {
        var (client, _) = await host.NewUserAsync();
        var id = (await Json(await client.PostAsJsonAsync("/engagements", new { name = "Abort me" }))).GetProperty("id").GetString();
        await client.PostAsJsonAsync($"/engagements/{id}/targets", new { locator = "svc", ownershipAttested = true });
        var (job, approval, _) = await SubmitCloudTriageAsync(client, id);
        Assert.Equal(id, approval.GetProperty("engagementId").GetString());

        var aborted = await Json(await client.PostAsync($"/engagements/{id}/abort", null));
        Assert.Equal(1, aborted.GetProperty("jobsCancelled").GetProperty("queued").GetInt32());

        Assert.Equal("Cancelled", (await client.GetFromJsonAsync<JsonElement>($"/jobs/{job.GetProperty("id").GetString()}")).GetProperty("state").GetString());
        Assert.Equal("Cancelled", (await client.GetFromJsonAsync<JsonElement>($"/approvals/{approval.GetProperty("id").GetString()}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Only_the_owner_can_see_or_decide_an_approval()
    {
        var (alice, _) = await host.NewUserAsync();
        var (bob, _) = await host.NewUserAsync();
        var (_, approval, _) = await SubmitCloudTriageAsync(alice);
        var approvalId = approval.GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/approvals/{approvalId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/approvals/{approvalId}/approve", null)).StatusCode);
        Assert.Equal(0, (await bob.GetFromJsonAsync<JsonElement>("/approvals")).GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/approvals")).StatusCode);

        // Still pending for its owner.
        Assert.Equal("Pending", (await alice.GetFromJsonAsync<JsonElement>($"/approvals/{approvalId}")).GetProperty("status").GetString());
    }
}
