using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cerberus.Api.Agents;
using Cerberus.Core.Models;
using Cerberus.Jobs;

namespace Cerberus.Tests;

/// <summary>Records what a piece of work reports, in order.</summary>
public sealed class RecordingReporter : IRunReporter
{
    public List<(string Type, string Json)> Events { get; } = [];
    public List<object> Findings { get; } = [];
    public System.Text.StringBuilder Text { get; } = new();

    public Task StepAsync(string id, string title, string status) => Task.CompletedTask;
    public Task ProgressAsync(int done, int total, string? message = null) => Task.CompletedTask;
    public Task FindingAsync(object finding) { Findings.Add(finding); return Task.CompletedTask; }
    public Task TextAsync(string channel, string delta) { Text.Append(delta); return Task.CompletedTask; }
    public Task FlushTextAsync() => Task.CompletedTask;
    public Task EventAsync(string type, object data) { Events.Add((type, JsonSerializer.Serialize(data))); return Task.CompletedTask; }
    public Task<OperatorAnswer?> AskAsync(OperatorQuestionSpec question, CancellationToken ct) => Task.FromResult<OperatorAnswer?>(null);
}

/// <summary>A model that plays back prepared steps, one per request, and records what it was sent.</summary>
public sealed class PlaybackProvider(params ChatEvent[][] steps) : IModelProvider
{
    public ModelProvider Provider => ModelProvider.Mock;
    public List<ChatRequest> Requests { get; } = [];

    public IAsyncEnumerable<string> StreamAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(request with { Turns = [.. request.Turns] });
        // Past the script: keep asking for the same tool, to test the step limit.
        foreach (var e in steps[Math.Min(Requests.Count - 1, steps.Length - 1)]) yield return e;
        await Task.Yield();
    }
}

public sealed class TempCode : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cerberus-agent-" + Guid.NewGuid().ToString("n"));

    public TempCode()
    {
        Directory.CreateDirectory(Path.Combine(Root, "target", "src"));
        Directory.CreateDirectory(Path.Combine(Root, "target", "node_modules", "dep"));
        File.WriteAllText(Path.Combine(Root, "target", "app.py"), "import os\nos.system(cmd)\npassword = \"hunter2hunter2\"\n");
        File.WriteAllText(Path.Combine(Root, "target", "src", "db.py"), "q = 'select ' + name\n");
        File.WriteAllText(Path.Combine(Root, "target", "node_modules", "dep", "x.py"), "os.system(1)\n");
        File.WriteAllBytes(Path.Combine(Root, "target", "blob.bin"), [1, 0, 2]);
        File.WriteAllText(Path.Combine(Root, "outside.txt"), "top secret\n");
        File.CreateSymbolicLink(Path.Combine(Root, "target", "link.txt"), Path.Combine(Root, "outside.txt"));
    }

    public CodeWorkspace Workspace(bool redact = false) => new(Root, "target", redact);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public class CodeToolsTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static IAgentTool Tool(CodeWorkspace workspace, string name) => CodeTools.For(workspace).Single(t => t.Definition.Name == name);

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("link.txt")]
    public async Task Nothing_outside_the_target_can_be_read(string path)
    {
        using var code = new TempCode();

        var problem = await Assert.ThrowsAsync<AgentToolException>(() =>
            Tool(code.Workspace(), CodeTools.ReadFile).RunAsync(J(JsonSerializer.Serialize(new { path })), default));

        Assert.DoesNotContain("top secret", problem.Message);
    }

    [Fact]
    public async Task A_file_is_read_with_line_numbers_and_a_listing_hides_links()
    {
        using var code = new TempCode();

        var text = await Tool(code.Workspace(), CodeTools.ReadFile).RunAsync(J("""{"path":"app.py"}"""), default);
        var listing = await Tool(code.Workspace(), CodeTools.ListDirectory).RunAsync(J("{}"), default);

        Assert.StartsWith("1\timport os\n2\tos.system(cmd)\n", text);
        var names = JsonDocument.Parse(listing).RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Contains("app.py", names);
        Assert.Contains("src", names);
        Assert.DoesNotContain("link.txt", names);
    }

    [Fact]
    public async Task For_a_cloud_model_credential_shaped_values_are_masked()
    {
        using var code = new TempCode();

        var local = await Tool(code.Workspace(), CodeTools.ReadFile).RunAsync(J("""{"path":"app.py"}"""), default);
        var cloud = await Tool(code.Workspace(redact: true), CodeTools.ReadFile).RunAsync(J("""{"path":"app.py"}"""), default);

        Assert.Contains("hunter2hunter2", local);
        Assert.DoesNotContain("hunter2hunter2", cloud);
        Assert.Contains("os.system(cmd)", cloud);
    }

    [Fact]
    public async Task Grep_searches_the_code_but_not_dependencies_binaries_or_links()
    {
        using var code = new TempCode();
        var grep = Tool(code.Workspace(), CodeTools.Grep);

        Assert.Equal("app.py:2: os.system(cmd)\n", await grep.RunAsync(J("""{"pattern":"os\\.system"}"""), default));
        Assert.Equal("src/db.py:1: q = 'select ' + name\n", await grep.RunAsync(J("""{"pattern":"select","path":"src"}"""), default));
        Assert.Equal("No matches.", await grep.RunAsync(J("""{"pattern":"top secret"}"""), default));
        await Assert.ThrowsAsync<AgentToolException>(() => grep.RunAsync(J("""{"pattern":"("}"""), default));
    }

    [Fact]
    public async Task Problems_are_told_to_the_model_not_thrown_at_the_run()
    {
        using var code = new TempCode();
        var read = Tool(code.Workspace(), CodeTools.ReadFile);

        Assert.Equal("No such file.", (await Assert.ThrowsAsync<AgentToolException>(() => read.RunAsync(J("""{"path":"nope.py"}"""), default))).Message);
        Assert.Equal("That is a binary file.", (await Assert.ThrowsAsync<AgentToolException>(() => read.RunAsync(J("""{"path":"blob.bin"}"""), default))).Message);
        Assert.Contains("use listDirectory", (await Assert.ThrowsAsync<AgentToolException>(() => read.RunAsync(J("""{"path":"src"}"""), default))).Message);
        Assert.Contains("only 3 line", (await Assert.ThrowsAsync<AgentToolException>(() => read.RunAsync(J("""{"path":"app.py","startLine":99}"""), default))).Message);
    }

    [Fact]
    public async Task A_finding_is_accepted_only_at_a_place_that_exists()
    {
        using var code = new TempCode();
        var findings = new List<AgentFinding>();
        var reporter = new RecordingReporter();
        var report = new SastAgentRunner.ReportFindingTool(code.Workspace(), findings, reporter);
        string Input(string file, int line, string severity = "High") => JsonSerializer.Serialize(
            new { title = "Command injection", severity, category = "injection", file, line, description = "cmd comes from the request.", cwe = "cwe-78" });

        Assert.Equal("Recorded as finding 1.", await report.RunAsync(J(Input("app.py", 2)), default));
        await Assert.ThrowsAsync<AgentToolException>(() => report.RunAsync(J(Input("app.py", 2)), default));          // twice
        await Assert.ThrowsAsync<AgentToolException>(() => report.RunAsync(J(Input("app.py", 40)), default));         // no such line
        await Assert.ThrowsAsync<AgentToolException>(() => report.RunAsync(J(Input("ghost.py", 1)), default));        // no such file
        await Assert.ThrowsAsync<AgentToolException>(() => report.RunAsync(J(Input("../outside.txt", 1)), default));  // outside
        await Assert.ThrowsAsync<AgentToolException>(() => report.RunAsync(J(Input("app.py", 1, "Catastrophic")), default));

        var finding = Assert.Single(findings);
        Assert.Equal(("agent.sast:app.py:2", "os.system(cmd)", "CWE-78", "model"), (finding.Id, finding.Evidence, finding.Cwe, finding.Confidence));
        Assert.Single(reporter.Findings);
    }
}

public class AgentLoopTests
{
    private sealed class EchoTool : IAgentTool
    {
        public ToolDefinition Definition { get; } = new("echo", "Repeats.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public Task<string> RunAsync(JsonElement input, CancellationToken ct) =>
            input.TryGetProperty("fail", out _)
                ? throw new AgentToolException("Told to fail.")
                : Task.FromResult("echoed " + input.GetProperty("say").GetString());
    }

    private static ChatEvent[] Calls(params (string Id, string Name, string Json)[] calls) =>
        [new TextDelta("Working. "), .. calls.Select(c => (ChatEvent)new ToolCallComplete(c.Id, c.Name, c.Json)), new UsageReport(10, 5), new StepFinished("tool_calls")];

    private static async Task<(AgentOutcome Outcome, RecordingReporter Reporter)> RunAsync(PlaybackProvider model, int maxSteps = 5)
    {
        var reporter = new RecordingReporter();
        var outcome = await new AgentLoop(new SingleProviderResolver(model))
            .RunAsync("echo", new ModelCredentials(), "system", "task", [new EchoTool()], maxSteps, reporter, default);
        return (outcome, reporter);
    }

    [Fact]
    public async Task Tools_are_run_their_results_go_back_to_the_model_and_everything_is_on_the_stream()
    {
        var model = new PlaybackProvider(
            Calls(("c1", "echo", """{"say":"hi"}""")),
            [new ReasoningDelta("thinking"), new ReasoningEnd("sig"), new TextDelta("All done."), new UsageReport(7, 3), new StepFinished("stop")]);

        var (outcome, reporter) = await RunAsync(model);

        Assert.Equal(new AgentOutcome("All done.", Steps: 2, ToolCalls: 1, HitStepLimit: false, InputTokens: 17, OutputTokens: 8), outcome);
        Assert.Equal(["tool_call", "tool_result"], reporter.Events.Select(e => e.Type).ToArray());
        Assert.Contains("\"input\":{\"say\":\"hi\"}", reporter.Events[0].Json);
        Assert.Contains("\"output\":\"echoed hi\"", reporter.Events[1].Json);
        Assert.Equal("Working. All done.", reporter.Text.ToString());

        // The second request carries the call and its result.
        var turns = model.Requests[1].Turns;
        Assert.Equal(["user", "assistant", "tool"], turns.Select(t => t.Role).ToArray());
        Assert.Equal("echoed hi", turns[2].Parts.OfType<ToolResultPart>().Single().Content);
    }

    [Fact]
    public async Task Bad_calls_are_answered_as_failures_so_the_model_can_correct_itself()
    {
        var model = new PlaybackProvider(
            Calls(("c1", "nope", "{}"), ("c2", "echo", "not json"), ("c3", "echo", """{"fail":true}""")),
            [new TextDelta("Sorry."), new StepFinished("stop")]);

        var (outcome, reporter) = await RunAsync(model);

        var results = model.Requests[1].Turns[2].Parts.OfType<ToolResultPart>().ToList();
        Assert.All(results, r => Assert.True(r.IsError));
        Assert.Equal(["There is no tool named \"nope\".", "The tool input is not valid JSON.", "Told to fail."], results.Select(r => r.Content).ToArray());
        Assert.Equal(3, outcome.ToolCalls);
        Assert.Equal(3, reporter.Events.Count(e => e.Type == "tool_result" && e.Json.Contains("\"isError\":true")));
    }

    [Fact]
    public async Task What_the_operator_says_mid_run_reaches_the_model_before_its_next_step()
    {
        var model = new PlaybackProvider(
            Calls(("c1", "echo", """{"say":"one"}""")),
            Calls(("c2", "echo", """{"say":"two"}""")),
            [new TextDelta("Done."), new StepFinished("stop")]);
        var inbox = new Queue<IReadOnlyList<string>>([[], ["Look at auth first.", "Skip the tests."], [], []]);
        var reporter = new RecordingReporter();

        await new AgentLoop(new SingleProviderResolver(model)).RunAsync(
            "echo", new ModelCredentials(), "system", "task", [new EchoTool()], 5, reporter, default,
            () => inbox.Count > 0 ? inbox.Dequeue() : []);

        // Step 1 saw only the task; step 2 got the operator's words after the tool result.
        Assert.Equal(["user"], model.Requests[0].Turns.Select(t => t.Role).ToArray());
        var second = model.Requests[1].Turns;
        Assert.Equal(["user", "assistant", "tool", "user"], second.Select(t => t.Role).ToArray());
        var said = ChatEvents.TextOf(second[3]);
        Assert.StartsWith("Message from the operator", said);
        Assert.Contains("Look at auth first.\n\nSkip the tests.", said);
        Assert.Equal(6, model.Requests[2].Turns.Count);   // nothing new was said before step 3
    }

    [Fact]
    public async Task A_message_that_arrives_as_the_agent_finishes_gets_one_more_step()
    {
        var model = new PlaybackProvider(
            [new TextDelta("Finished."), new StepFinished("stop")],
            [new TextDelta("Checked that too."), new StepFinished("stop")]);
        var inbox = new Queue<IReadOnlyList<string>>([[], ["Also check the login."], [], []]);

        var outcome = await new AgentLoop(new SingleProviderResolver(model)).RunAsync(
            "echo", new ModelCredentials(), "system", "task", [new EchoTool()], 5, new RecordingReporter(), default,
            () => inbox.Count > 0 ? inbox.Dequeue() : []);

        Assert.Equal(("Checked that too.", 2), (outcome.FinalText, outcome.Steps));
        Assert.Equal(["user", "assistant", "user"], model.Requests[1].Turns.Select(t => t.Role).ToArray());
    }

    [Fact]
    public async Task An_agent_that_never_stops_asking_is_stopped_at_its_step_limit()
    {
        var model = new PlaybackProvider(Calls(("c1", "echo", """{"say":"again"}""")));

        var (outcome, _) = await RunAsync(model, maxSteps: 3);

        Assert.True(outcome.HitStepLimit);
        Assert.Equal((3, 3), (outcome.Steps, outcome.ToolCalls));
        Assert.Equal(3, model.Requests.Count);
    }
}

[Collection("api")]
public class SastAgentApiTests(ApiHost host)
{
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

    [Fact]
    public async Task The_agent_reads_the_code_reports_a_finding_and_every_tool_call_is_on_the_stream()
    {
        var (client, _) = await host.NewUserAsync();

        // A local model: no approval, the job starts at once.
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "svc", model = "llama3.1" }));
        Assert.Equal("Queued", submitted.GetProperty("state").GetString());
        var job = await WaitForEndAsync(client, submitted.GetProperty("id").GetString()!);
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());

        // The stream: each call followed by its result, and the finding as it was reported.
        var events = (await client.GetFromJsonAsync<JsonElement>($"/jobs/{job.GetProperty("id").GetString()}/events?after=0"))
            .GetProperty("events").EnumerateArray().ToList();
        var tools = events.Where(e => e.GetProperty("type").GetString() is "tool_call" or "tool_result")
            .Select(e => $"{e.GetProperty("type").GetString()}:{e.GetProperty("data").GetProperty("name").GetString()}").ToArray();
        Assert.Equal(
        [
            "tool_call:listDirectory", "tool_result:listDirectory",
            "tool_call:readFile", "tool_result:readFile",
            "tool_call:reportFinding", "tool_result:reportFinding",
        ], tools);
        var read = events.First(e => e.GetProperty("type").GetString() == "tool_call" && e.GetProperty("data").GetProperty("name").GetString() == "readFile");
        Assert.Equal("loader.py", read.GetProperty("data").GetProperty("input").GetProperty("path").GetString());
        var streamed = events.Single(e => e.GetProperty("type").GetString() == "finding").GetProperty("data");
        Assert.Equal("agent.sast:loader.py:2", streamed.GetProperty("id").GetString());
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "text");

        // The stored run.
        var run = await client.GetFromJsonAsync<JsonElement>($"/agent-runs/{job.GetProperty("resultId").GetString()}");
        Assert.True(run.GetProperty("complete").GetBoolean());
        Assert.Equal(("sast", "llama3.1", 3, 1), (run.GetProperty("agent").GetString(), run.GetProperty("model").GetString(),
            run.GetProperty("toolCalls").GetInt32(), run.GetProperty("findingCount").GetInt32()));
        var finding = run.GetProperty("findings")[0];
        Assert.Equal("obj = pickle.loads(blob)", finding.GetProperty("evidence").GetString());
        Assert.Equal("model", finding.GetProperty("confidence").GetString());
        Assert.StartsWith("Read 1 file(s) and reported 1 finding(s).", run.GetProperty("summary").GetString());

        // A model's findings are not a scan and touch no compliance result.
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/scans")).GetArrayLength());
        var audit = (await client.GetFromJsonAsync<JsonElement>("/audit?limit=50")).EnumerateArray()
            .Single(e => e.GetProperty("action").GetString() == "agent.run");
        Assert.Contains("sent to a local model", audit.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task With_a_cloud_model_nothing_is_read_or_sent_until_the_operator_approves()
    {
        var (client, _) = await host.NewUserAsync();
        var sentBefore = host.Cloud.Requests.Count;

        var submitted = await Json(await client.PostAsJsonAsync("/jobs",
            new { type = "sast", path = "app", model = "claude-haiku-4-5", maxSteps = 5, instructions = "Look at the hashing." }));
        var approval = submitted.GetProperty("approval");
        Assert.Equal("send-code-to-cloud-model", approval.GetProperty("kind").GetString());
        Assert.Equal("Let claude-haiku-4-5 (Anthropic) read the code under \"app\"", approval.GetProperty("title").GetString());
        Assert.Contains("up to 5 steps", approval.GetProperty("detail").GetString());
        Assert.Equal(sentBefore, host.Cloud.Requests.Count);

        await client.PostAsJsonAsync($"/approvals/{approval.GetProperty("id").GetString()}/approve", new { });
        var job = await WaitForEndAsync(client, submitted.GetProperty("job").GetProperty("id").GetString()!);

        Assert.Equal("Succeeded", job.GetProperty("state").GetString());
        Assert.Equal(sentBefore + 1, host.Cloud.Requests.Count);
        Assert.Contains("Look at the hashing.", host.Cloud.Requests[^1].Messages[^1].Content);
        var audit = (await client.GetFromJsonAsync<JsonElement>("/audit?limit=50")).EnumerateArray()
            .Single(e => e.GetProperty("action").GetString() == "agent.run");
        Assert.Contains("sent to Anthropic", audit.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Requests_are_validated_a_bad_target_fails_the_job_and_runs_are_private()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "app" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "app", model = "gpt-0" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "app", model = "llama3.1", maxSteps = 51 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "app", model = "llama3.1", instructions = new string('x', 2001) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "../x", model = "llama3.1" })).StatusCode);

        var missing = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "no-such-dir", model = "llama3.1" }));
        var failed = await WaitForEndAsync(client, missing.GetProperty("id").GetString()!);
        Assert.Equal("Failed", failed.GetProperty("state").GetString());

        var ok = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "other", model = "llama3.1" }));
        var job = await WaitForEndAsync(client, ok.GetProperty("id").GetString()!);
        var runId = job.GetProperty("resultId").GetString();
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>($"/agent-runs/{runId}")).GetProperty("findingCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/agent-runs/{runId}")).StatusCode);
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/agent-runs")).GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/agent-runs")).StatusCode);
    }

    [Fact]
    public async Task The_operator_can_steer_a_running_agent_and_only_a_running_one()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();

        // Hold the model at its first step so the message is certain to arrive mid-run.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Tools.BeforeReply = async _ => { reached.TrySetResult(); await release.Task; };
        string jobId;
        try
        {
            var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "svc", model = "llama3.1" }));
            jobId = submitted.GetProperty("id").GetString()!;
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/jobs/{jobId}/messages", new { text = "  " })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/jobs/{jobId}/messages", new { text = "hi" })).StatusCode);
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync($"/jobs/{jobId}/messages", new { text = "Stop, that is enough." })).StatusCode);
        }
        finally
        {
            host.Tools.BeforeReply = null;
            release.TrySetResult();
        }

        var job = await WaitForEndAsync(client, jobId);
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());

        // The agent listed the directory, then read the message instead of going on.
        var run = await client.GetFromJsonAsync<JsonElement>($"/agent-runs/{job.GetProperty("resultId").GetString()}");
        Assert.StartsWith("The operator said: \"Stop, that is enough.\"", run.GetProperty("summary").GetString());
        Assert.Equal(1, run.GetProperty("toolCalls").GetInt32());

        var events = (await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}/events?after=0")).GetProperty("events").EnumerateArray().ToList();
        var message = events.Single(e => e.GetProperty("type").GetString() == "operator_message");
        Assert.Equal("Stop, that is enough.", message.GetProperty("data").GetProperty("text").GetString());
        Assert.Contains((await client.GetFromJsonAsync<JsonElement>("/audit?limit=50")).EnumerateArray(),
            e => e.GetProperty("action").GetString() == "job.message");

        // Finished: nobody is listening any more.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/jobs/{jobId}/messages", new { text = "hello?" })).StatusCode);
        // A job without an agent never takes messages.
        var scan = await Json(await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false }));
        await WaitForEndAsync(client, scan.GetProperty("id").GetString()!);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/jobs/{scan.GetProperty("id").GetString()}/messages", new { text = "hi" })).StatusCode);
    }

    [Fact]
    public async Task A_review_can_dispatch_the_agent_as_one_of_its_steps_and_reports_what_it_found()
    {
        var (client, _) = await host.NewUserAsync();

        var submitted = await Json(await client.PostAsJsonAsync("/jobs",
            new { type = "review", path = "svc", sca = false, exclusions = "honour", catalogs = new[] { "owasp-top10" }, agentModel = "llama3.1", maxSteps = 10 }));
        var approval = submitted.GetProperty("approval");
        Assert.Equal("run-plan", approval.GetProperty("kind").GetString());   // a local model: only the plan is asked about
        Assert.Contains("2. Have llama3.1 read the code", approval.GetProperty("detail").GetString());
        Assert.Contains("up to 10 steps", approval.GetProperty("detail").GetString());

        await client.PostAsJsonAsync($"/approvals/{approval.GetProperty("id").GetString()}/approve", new { });
        var job = await WaitForEndAsync(client, submitted.GetProperty("job").GetProperty("id").GetString()!);
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());

        var events = (await client.GetFromJsonAsync<JsonElement>($"/jobs/{job.GetProperty("id").GetString()}/events?after=0"))
            .GetProperty("events").EnumerateArray().ToList();
        var steps = events.Where(e => e.GetProperty("type").GetString() == "step")
            .Select(e => e.GetProperty("data").GetProperty("id").GetString()!).Distinct().ToArray();
        Assert.Equal(["scan", "sast", "assess:owasp-top10", "report"], steps);
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "tool_call");

        var review = await client.GetFromJsonAsync<JsonElement>($"/reviews/{job.GetProperty("resultId").GetString()}");
        var run = await client.GetFromJsonAsync<JsonElement>($"/agent-runs/{review.GetProperty("agentRunId").GetString()}");
        Assert.Equal(1, run.GetProperty("findingCount").GetInt32());
        var report = review.GetProperty("report").GetString()!;
        Assert.Contains("## Reported by the model (`llama3.1`)", report);
        Assert.Contains("`loader.py:2`", report);
        Assert.Contains("do not change any compliance result", report);

        // With a cloud agent model, the plan's approval also covers sending code out.
        var cloud = await Json(await client.PostAsJsonAsync("/jobs",
            new { type = "review", path = "app", sca = false, catalogs = new[] { "owasp-top10" }, agentModel = "claude-haiku-4-5" }));
        Assert.Equal(["run-plan", "send-code-to-cloud-model"],
            cloud.GetProperty("approval").GetProperty("kinds").EnumerateArray().Select(k => k.GetString()!).ToArray());
        await client.PostAsync($"/approvals/{cloud.GetProperty("approval").GetProperty("id").GetString()}/deny", null);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/jobs", new { type = "review", path = "app", agentModel = "nope" })).StatusCode);
    }
}
