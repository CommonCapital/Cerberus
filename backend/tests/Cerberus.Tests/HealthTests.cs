using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api.Agents;
using Cerberus.Api.Health;
using Cerberus.Core.Models;
using Cerberus.Models;

namespace Cerberus.Tests;

public class ReadinessServiceTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Check(string name, bool required, Func<CancellationToken, Task<(bool, string)>> run) : IReadinessCheck
    {
        public int Runs { get; private set; }
        public string Name => name;
        public bool Required => required;

        public Task<(bool, string)> CheckAsync(CancellationToken ct)
        {
            Runs++;
            return run(ct);
        }
    }

    private static Check Passing(string name, bool required = true) => new(name, required, _ => Task.FromResult((true, "fine")));
    private static Check Failing(string name, bool required) => new(name, required, _ => Task.FromResult((false, "is missing")));

    [Fact]
    public async Task With_everything_in_place_the_service_is_healthy()
    {
        var report = await new ReadinessService([Passing("database"), Passing("embeddings", required: false)], new Clock()).CheckAsync(default);

        Assert.Equal(HealthState.Healthy, report.Status);
        Assert.Equal([("database", HealthState.Healthy, "fine"), ("embeddings", HealthState.Healthy, "fine")],
            report.Checks.Select(c => (c.Name, c.Status, c.Detail)).ToArray());
    }

    [Fact]
    public async Task Something_optional_missing_is_degraded_and_something_required_is_unhealthy()
    {
        var degraded = await new ReadinessService([Passing("database"), Failing("embeddings", required: false)], new Clock()).CheckAsync(default);
        var unhealthy = await new ReadinessService([Failing("database", required: true), Failing("embeddings", required: false)], new Clock()).CheckAsync(default);

        Assert.Equal(HealthState.Degraded, degraded.Status);
        Assert.Equal(HealthState.Degraded, degraded.Checks[1].Status);
        Assert.Equal(HealthState.Unhealthy, unhealthy.Status);
        Assert.Equal([HealthState.Unhealthy, HealthState.Degraded], unhealthy.Checks.Select(c => c.Status).ToArray());
    }

    [Fact]
    public async Task A_check_that_throws_is_a_failed_check_and_what_it_threw_is_not_published()
    {
        var check = new Check("database", true, _ => throw new IOException("Access to /srv/secret/path/cerberus.db denied for user svc_cerberus"));

        var report = await new ReadinessService([check, Passing("skills", required: false)], new Clock()).CheckAsync(default);

        Assert.Equal(HealthState.Unhealthy, report.Status);
        Assert.Equal("failed (IOException)", report.Checks[0].Detail);
        Assert.Equal(HealthState.Healthy, report.Checks[1].Status);   // the others are still checked
    }

    [Fact]
    public async Task A_check_that_hangs_is_cut_off_and_counted_as_failed()
    {
        var hanging = new Check("embeddings", false, async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return (true, "never");
        });
        var service = new ReadinessService([hanging, Passing("database")], new Clock()) { CheckTimeout = TimeSpan.FromMilliseconds(100) };

        var report = await service.CheckAsync(default);

        Assert.Equal(HealthState.Degraded, report.Status);
        Assert.Equal("did not answer within 0.1 s", report.Checks[0].Detail);
    }

    [Fact]
    public async Task A_probe_every_second_does_not_run_the_checks_every_second()
    {
        var clock = new Clock();
        var check = Passing("database");
        var service = new ReadinessService([check], clock);

        await service.CheckAsync(default);
        clock.Now += TimeSpan.FromSeconds(2);
        await service.CheckAsync(default);
        Assert.Equal(1, check.Runs);

        clock.Now += TimeSpan.FromSeconds(4);
        await service.CheckAsync(default);
        Assert.Equal(2, check.Runs);
    }

    [Fact]
    public async Task The_worker_is_ready_once_started_and_not_after_it_stopped_on_an_error()
    {
        var worker = new WorkerHealth();
        var check = new JobWorkerCheck(worker);

        Assert.Equal((false, "not started yet"), await check.CheckAsync(default));
        worker.StartedAt = DateTime.UtcNow;
        Assert.Equal((true, "running"), await check.CheckAsync(default));
        worker.Fault = "InvalidOperationException";
        Assert.False((await check.CheckAsync(default)).Item1);
    }

    [Fact]
    public async Task The_key_ring_is_made_on_a_fresh_installation_and_must_be_writable()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cerberus-keys-{Guid.NewGuid():n}");
        try
        {
            var (ok, detail) = await new KeyRingCheck(new HealthPaths(directory)).CheckAsync(default);

            Assert.Equal((true, "writable"), (ok, detail));
            Assert.True(Directory.Exists(directory));
            Assert.Empty(Directory.GetFiles(directory));   // the probe file is gone
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

[Collection("api")]
public class HealthApiTests(ApiHost host)
{
    [Fact]
    public async Task Liveness_only_says_the_process_answers()
    {
        var response = await host.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readiness_reports_each_dependency_to_anyone_without_giving_anything_away()
    {
        var response = await host.CreateClient().GetAsync("/health/ready");   // no sign-in: a load balancer has none

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var report = JsonDocument.Parse(body).RootElement;
        Assert.Equal("healthy", report.GetProperty("status").GetString());
        var checks = report.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c);
        Assert.Equal(["catalogs", "database", "embeddings", "job-worker", "key-ring", "scan-root", "skills"], checks.Keys.Order().ToArray());
        Assert.All(checks.Values, c => Assert.Equal("healthy", c.GetProperty("status").GetString()));
        Assert.Equal("running", checks["job-worker"].GetProperty("detail").GetString());
        Assert.EndsWith(" loaded", checks["skills"].GetProperty("detail").GetString());

        // Nothing an anonymous caller could use: no paths, no addresses.
        Assert.DoesNotContain(Path.GetTempPath().TrimEnd('/'), body);
        Assert.DoesNotContain(Environment.GetEnvironmentVariable("CERBERUS_SCAN_ROOT") ?? "\u0000", body);
        Assert.DoesNotContain("http://", body);
    }

    [Fact]
    public async Task The_health_command_turns_the_report_into_an_exit_code()
    {
        var output = new StringWriter();

        var code = await ServiceCommands.HealthAsync("http://localhost", output, host.CreateClient(), default);

        Assert.Equal(ServiceCommands.Ok, code);
        Assert.Equal("healthy", output.ToString().Trim());
    }
}

public class ServiceCommandTests
{
    private sealed class Answers(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class Refuses : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused");
    }

    private static async Task<(int Code, string Said)> Health(HttpMessageHandler handler)
    {
        var output = new StringWriter();
        var code = await ServiceCommands.HealthAsync("http://localhost:3000", output, new HttpClient(handler), default);
        return (code, output.ToString());
    }

    [Fact]
    public async Task A_degraded_service_is_still_ready_and_the_command_says_what_is_missing()
    {
        var (code, said) = await Health(new Answers(HttpStatusCode.OK,
            """{"status":"degraded","checks":[{"name":"database","status":"healthy","detail":"reachable"},{"name":"embeddings","status":"degraded","detail":"the embedding server cannot be reached; semantic code search is unavailable"}]}"""));

        Assert.Equal(ServiceCommands.Ok, code);
        Assert.Equal("degraded\n  embeddings: degraded — the embedding server cannot be reached; semantic code search is unavailable\n", said.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task An_unhealthy_service_a_silent_one_and_one_that_is_something_else_all_fail_the_check()
    {
        var unhealthy = await Health(new Answers(HttpStatusCode.ServiceUnavailable,
            """{"status":"unhealthy","checks":[{"name":"database","status":"unhealthy","detail":"cannot be opened"}]}"""));
        var silent = await Health(new Refuses());
        var other = await Health(new Answers(HttpStatusCode.OK, "<html>Welcome to nginx</html>"));

        Assert.Equal(ServiceCommands.Failed, unhealthy.Code);
        Assert.Contains("database: unhealthy — cannot be opened", unhealthy.Said);
        Assert.Equal((ServiceCommands.Failed, "The service does not answer at http://localhost:3000."), (silent.Code, silent.Said.Trim()));
        Assert.Equal(ServiceCommands.Failed, other.Code);
        Assert.Contains("not a health report", other.Said);
    }

    private static async Task<(int Code, string Said)> Smoke(IModelProvider provider, string? model = "echo")
    {
        var output = new StringWriter();
        var code = await ServiceCommands.SmokeAsync(new AgentLoop(new SingleProviderResolver(provider)), model, new ModelCredentials(), output, default);
        return (code, output.ToString());
    }

    [Fact]
    public async Task A_model_that_calls_the_tool_and_answers_from_its_result_passes()
    {
        // The dev model follows rules: asked to read a file it calls readFile, then reports what came back.
        var (code, said) = await Smoke(new MockProvider());

        Assert.Equal(ServiceCommands.Ok, code);
        Assert.StartsWith("echo: ok", said);
        Assert.Contains("called the tool with valid arguments: yes (path \"smoke.txt\")", said);
        Assert.Contains("used the tool's result in its answer: yes", said);
    }

    [Fact]
    public async Task A_model_that_answers_without_calling_the_tool_fails_and_so_does_one_that_ignores_what_came_back()
    {
        var guesses = await Smoke(new PlaybackProvider([new TextDelta("The word is probably BANANA."), new StepFinished("stop")]));
        var ignores = await Smoke(new PlaybackProvider(
            [new ToolCallComplete("c1", "readFile", """{"path":"smoke.txt"}"""), new StepFinished("tool_calls")],
            [new TextDelta("I have read the file."), new StepFinished("stop")]));

        Assert.Equal(ServiceCommands.Failed, guesses.Code);
        Assert.Contains("called the tool with valid arguments: no", guesses.Said);
        Assert.Contains("does not call tools", guesses.Said);
        Assert.Equal(ServiceCommands.Failed, ignores.Code);
        Assert.Contains("called the tool with valid arguments: yes", ignores.Said);
        Assert.Contains("used the tool's result in its answer: no", ignores.Said);
    }

    [Fact]
    public async Task A_model_that_cannot_be_reached_fails_with_the_reason_and_an_unknown_one_is_a_usage_error()
    {
        var unreachable = await Smoke(new ScriptedProvider(ModelProvider.Mock, _ => throw new HttpRequestException("Connection refused (localhost:11434)")));
        var unknown = await Smoke(new MockProvider(), "gpt-0");
        var unnamed = await Smoke(new MockProvider(), null);

        Assert.Equal(ServiceCommands.Failed, unreachable.Code);
        Assert.Contains("the model could not be called: Connection refused", unreachable.Said);
        Assert.Equal(ServiceCommands.Usage, unknown.Code);
        Assert.Contains("\"gpt-0\" is not a model this server knows.", unknown.Said);
        Assert.Contains("Known models:", unknown.Said);
        Assert.Equal(ServiceCommands.Usage, unnamed.Code);
    }
}
