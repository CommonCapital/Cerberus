using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api.Agents;
using Cerberus.Api.Audit;
using Cerberus.Api.Web;
using Cerberus.Authorization;
using Cerberus.Core.Models;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace Cerberus.Tests;

/// <summary>A reporter whose operator answers from a script; a question beyond the script goes unanswered.</summary>
public sealed class AnsweringReporter(params OperatorAnswer?[] answers) : IRunReporter
{
    private readonly Queue<OperatorAnswer?> _answers = new(answers);

    public List<OperatorQuestionSpec> Asked { get; } = [];
    public List<(string Type, string Json)> Events { get; } = [];

    public Task StepAsync(string id, string title, string status) => Task.CompletedTask;
    public Task ProgressAsync(int done, int total, string? message = null) => Task.CompletedTask;
    public Task FindingAsync(object finding) => Task.CompletedTask;
    public Task TextAsync(string channel, string delta) => Task.CompletedTask;
    public Task FlushTextAsync() => Task.CompletedTask;
    public Task EventAsync(string type, object data) { Events.Add((type, JsonSerializer.Serialize(data))); return Task.CompletedTask; }

    public Task<OperatorAnswer?> AskAsync(OperatorQuestionSpec question, CancellationToken ct)
    {
        Asked.Add(question);
        return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
    }
}

/// <summary>A tool whose calls change something outside, and say so.</summary>
public sealed class TicketTool : IAgentTool
{
    public List<string> Made { get; } = [];

    public ToolDefinition Definition { get; } = new("createTicket", "Opens a ticket in the tracker.",
        JsonDocument.Parse("""{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""").RootElement.Clone());

    public ToolEffects Effects => ToolEffects.SendsOut | ToolEffects.ChangesOutside;
    public string? AskFirst => ActionKinds.ChangeOutsideSystems;
    public string Describe(JsonElement input) => "open a ticket in the tracker";

    public Task<string> RunAsync(JsonElement input, CancellationToken ct)
    {
        Made.Add(input.GetProperty("title").GetString()!);
        return Task.FromResult($"Ticket {Made.Count} opened.");
    }
}

public class ToolGateLoopTests
{
    private sealed class ScriptedGate(params GateDecision[] decisions) : IToolGate
    {
        private readonly Queue<GateDecision> _decisions = new(decisions);
        public List<string> AskedAbout { get; } = [];

        public Task<GateDecision> DecideAsync(IAgentTool tool, JsonElement input, CancellationToken ct)
        {
            AskedAbout.Add($"{tool.Definition.Name} {input.GetRawText()}");
            return Task.FromResult(_decisions.Dequeue());
        }
    }

    private sealed class ReadTool : IAgentTool
    {
        public int Calls { get; private set; }
        public ToolDefinition Definition { get; } = new("readFile", "Reads a file.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());
        public Task<string> RunAsync(JsonElement input, CancellationToken ct) { Calls++; return Task.FromResult("contents"); }
    }

    private static ChatEvent[] Call(string id, string tool, string json) => [new ToolCallComplete(id, tool, json), new StepFinished("tool_calls")];
    private static ChatEvent[] Say(string text) => [new TextDelta(text), new StepFinished("stop")];

    private static async Task<(AgentOutcome Outcome, PlaybackProvider Model, RecordingReporter Reporter)> RunAsync(
        PlaybackProvider model, IReadOnlyList<IAgentTool> tools, IToolGate? gate)
    {
        var reporter = new RecordingReporter();
        var outcome = await new AgentLoop(new SingleProviderResolver(model))
            .RunAsync("echo", new ModelCredentials(), "system", "task", tools, 6, reporter, default, gate: gate);
        return (outcome, model, reporter);
    }

    private static ToolResultPart ResultOf(PlaybackProvider model, int request, string callId) =>
        model.Requests[request].Turns.SelectMany(t => t.Parts.OfType<ToolResultPart>()).Single(r => r.ToolCallId == callId);

    [Fact]
    public async Task A_call_that_needs_consent_runs_only_on_a_yes_and_a_no_reaches_the_model_with_its_reason()
    {
        var tickets = new TicketTool();
        var gate = new ScriptedGate(new GateDecision(false, "The operator did not allow this call and writes: \"not in production\"."), GateDecision.Yes);
        var model = new PlaybackProvider(
            Call("c1", "createTicket", """{"title":"Rotate the key"}"""),
            Call("c2", "createTicket", """{"title":"Rotate the staging key"}"""),
            Say("One ticket opened."));

        var (outcome, _, reporter) = await RunAsync(model, [tickets], gate);

        // The first was refused and never made; the second, allowed, was.
        Assert.Equal(["Rotate the staging key"], tickets.Made);
        Assert.Equal(["""createTicket {"title":"Rotate the key"}""", """createTicket {"title":"Rotate the staging key"}"""], gate.AskedAbout);
        var refused = ResultOf(model, 1, "c1");
        Assert.True(refused.IsError);
        Assert.Equal("The operator did not allow this call and writes: \"not in production\".", refused.Content);
        Assert.Equal(("Ticket 1 opened.", false), (ResultOf(model, 2, "c2").Content, ResultOf(model, 2, "c2").IsError));
        Assert.Equal("One ticket opened.", outcome.FinalText);

        // On the run the call is shown before it is decided, and its result says how it ended.
        Assert.Equal(["tool_call", "tool_result", "tool_call", "tool_result"], reporter.Events.Select(e => e.Type).ToArray());
        Assert.Contains("\"isError\":true", reporter.Events[1].Json);
    }

    [Fact]
    public async Task A_run_with_nobody_to_ask_does_not_make_such_a_call_at_all()
    {
        var tickets = new TicketTool();
        var model = new PlaybackProvider(Call("c1", "createTicket", """{"title":"x"}"""), Say("Could not."));

        var (_, played, _) = await RunAsync(model, [tickets], gate: null);

        Assert.Empty(tickets.Made);
        Assert.Equal("This run has nobody to ask, and this call needs the operator's consent: it was not made. Go on without it.", ResultOf(played, 1, "c1").Content);
    }

    [Fact]
    public async Task A_tool_that_only_reads_never_comes_to_the_gate_and_a_malformed_call_is_not_asked_about()
    {
        var reads = new ReadTool();
        var tickets = new TicketTool();
        var gate = new ScriptedGate();   // any question would fail: nothing is scripted
        var model = new PlaybackProvider(
            Call("c1", "readFile", """{"path":"a.py"}"""),
            Call("c2", "createTicket", "not json"),
            Say("Done."));

        var (_, played, _) = await RunAsync(model, [reads, tickets], gate);

        Assert.Equal(1, reads.Calls);
        Assert.Empty(gate.AskedAbout);   // the operator is not bothered with a call that could not have run anyway
        Assert.Equal("The tool input is not valid JSON.", ResultOf(played, 2, "c2").Content);
        // What a tool does is its own declaration; one that says nothing only reads and asks nothing.
        Assert.Equal((ToolEffects.None, null), (((IAgentTool)reads).Effects, ((IAgentTool)reads).AskFirst));
        Assert.Equal("use the tool \"readFile\"", ((IAgentTool)reads).Describe(default));
    }
}

public class AskOperatorToolTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task The_question_goes_to_the_operator_and_their_answer_comes_back_as_theirs()
    {
        var reporter = new AnsweringReporter(new OperatorAnswer(["Staging"], null), new OperatorAnswer([], "only the payment service"), new OperatorAnswer(["Yes"], "but be quick"));
        var tool = new AskOperatorTool(reporter);

        var first = await tool.RunAsync(J("""{"question":"Which environment is this configuration for?","options":["Production","Staging"]}"""), default);
        var second = await tool.RunAsync(J("""{"question":"What should I look at first?"}"""), default);
        var third = await tool.RunAsync(J("""{"question":"Is the legacy importer in scope?","options":["Yes","No"]}"""), default);

        Assert.Equal("The operator answered: Staging", first);
        Assert.Equal("The operator answered: only the payment service", second);
        Assert.Equal("The operator answered: Yes — but be quick", third);
        var asked = reporter.Asked[0];
        Assert.Equal(("Which environment is this configuration for?", true), (asked.Text, asked.AllowText));   // always answerable in their own words
        Assert.Equal(["Production", "Staging"], asked.Options.Select(o => o.Label));
    }

    [Fact]
    public async Task An_agent_cannot_hand_its_work_back_one_question_at_a_time()
    {
        var tool = new AskOperatorTool(new AnsweringReporter(new OperatorAnswer([], "a"), new OperatorAnswer([], "b"), new OperatorAnswer([], "c"), new OperatorAnswer([], "d")));
        for (var i = 0; i < AskOperatorTool.MaxQuestions; i++) await tool.RunAsync(J($$"""{"question":"Question {{i}}?"}"""), default);

        var refused = await Assert.ThrowsAsync<AgentToolException>(() => tool.RunAsync(J("""{"question":"One more?"}"""), default));

        Assert.Equal("You have asked your 3 questions in this run. Go on with what you know and say what you assumed.", refused.Message);
    }

    [Fact]
    public async Task Nobody_answering_is_an_answer_and_a_malformed_question_is_not_asked()
    {
        var reporter = new AnsweringReporter();   // nobody is there
        var tool = new AskOperatorTool(reporter);

        Assert.StartsWith("Nobody answered. Go on with the safest assumption", await tool.RunAsync(J("""{"question":"Which environment?"}"""), default));

        async Task<string> WhyAsync(string json) => (await Assert.ThrowsAsync<AgentToolException>(() => tool.RunAsync(J(json), default))).Message;
        Assert.Equal("question is required.", await WhyAsync("""{"question":"  "}"""));
        Assert.Equal("Keep the question under 600 characters.", await WhyAsync($$"""{"question":"{{new string('x', 601)}}"}"""));
        Assert.Equal("Offer at most 6 options.", await WhyAsync("""{"question":"Which?","options":["1","2","3","4","5","6","7"]}"""));
        Assert.Equal("Option labels must be unique.", await WhyAsync("""{"question":"Which?","options":["Yes","yes"]}"""));
        Assert.Single(reporter.Asked);   // none of the refused ones reached a person, or counted against the limit
    }

    [Fact]
    public async Task What_is_shown_to_a_person_is_masked_because_a_model_may_quote_what_it_read()
    {
        var reporter = new AnsweringReporter(new OperatorAnswer(["No"], null));
        var tool = new AskOperatorTool(reporter);

        await tool.RunAsync(J("""{"question":"Is the key AKIAIOSFODNN7EXAMPLE still in use?","options":["No","Yes, AKIAIOSFODNN7EXAMPLE is live"]}"""), default);

        var asked = reporter.Asked.Single();
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", asked.Text + string.Concat(asked.Options.Select(o => o.Label)));
    }
}

[Collection("api")]
public class InterruptApiTests(ApiHost host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> WaitForAsync(HttpClient client, string jobId, params string[] states)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}");
            if (states.Contains(job.GetProperty("state").GetString())) return job;
            Assert.True(DateTime.UtcNow < deadline, $"job is {job.GetProperty("state").GetString()}, expected one of {string.Join("/", states)}");
            await Task.Delay(30);
        }
    }

    private static Task<JsonElement> EndAsync(HttpClient client, string jobId) => WaitForAsync(client, jobId, "Succeeded", "Failed", "Cancelled", "TimedOut");

    private static async Task<JsonElement> QuestionAsync(HttpClient client, string jobId)
    {
        await WaitForAsync(client, jobId, "AwaitingInput");
        return (await client.GetFromJsonAsync<JsonElement>($"/questions?status=pending&jobId={jobId}")).EnumerateArray().Single();
    }

    private static async Task AnswerAsync(HttpClient client, JsonElement question, string? option, string? text = null) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/questions/{question.GetProperty("id").GetString()}/answer",
            new { selected = option is null ? [] : new[] { option }, text })).StatusCode);

    private static async Task<List<JsonElement>> EventsAsync(HttpClient client, string jobId) =>
        [.. (await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}/events")).GetProperty("events").EnumerateArray()];

    private static string ResultOf(IEnumerable<JsonElement> events, string tool) => events
        .Where(e => e.GetProperty("type").GetString() == "tool_result" && e.GetProperty("data").GetProperty("name").GetString() == tool)
        .Select(e => e.GetProperty("data").GetProperty("output").GetString()!).Last();

    private static async Task<List<string>> AuditAsync(HttpClient client) =>
        [.. (await client.GetFromJsonAsync<JsonElement>("/audit")).EnumerateArray().Select(e => $"{e.GetProperty("action").GetString()}:{e.GetProperty("outcome").GetString()}")];

    /// <summary>A server with one tool the administrator named although the server does not call it read-only.</summary>
    private static McpServerState Tracker() => new(
        "tracker", "https://tracker.example/mcp", true, null, null, "Tracker", "2025-06-18",
        [new ExposedTool("tracker", "create_ticket", "mcp_tracker_create_ticket", "The tool \"create_ticket\" of the outside server \"tracker\".",
            JsonDocument.Parse("""{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""").RootElement.Clone(), ChangesThings: true)],
        []);

    /// <summary>Starts a code review with research on, and gives the run its ordinary approval (the internet and the servers).</summary>
    private static async Task<string> StartResearchAsync(HttpClient client)
    {
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "svc", model = "llama3.1", research = true }));
        if (submitted.TryGetProperty("approval", out var approval))
        {
            Assert.Contains("asks you again before each call", approval.GetProperty("detail").GetString());
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/approvals/{approval.GetProperty("id").GetString()}/approve", new { remember = true })).StatusCode);
            return submitted.GetProperty("job").GetProperty("id").GetString()!;
        }
        return submitted.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task An_agent_told_that_a_person_is_there_may_ask_and_goes_on_with_the_answer()
    {
        var (client, _) = await host.NewUserAsync();

        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "svc", model = "llama3.1", questions = true, instructions = "Ask me first what matters." }));
        var jobId = submitted.GetProperty("id").GetString()!;

        // The run waits, with the agent's question and its options — and room for an answer in the operator's own words.
        var question = await QuestionAsync(client, jobId);
        Assert.Equal("Which part of this code matters most to you?", question.GetProperty("text").GetString());
        Assert.Equal(["The loaders", "Everything equally"], question.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetString()!).ToArray());
        Assert.True(question.GetProperty("allowText").GetBoolean());

        await AnswerAsync(client, question, "The loaders");
        Assert.Equal("Succeeded", (await EndAsync(client, jobId)).GetProperty("state").GetString());
        var events = await EventsAsync(client, jobId);
        Assert.Equal("The operator answered: The loaders", ResultOf(events, "askOperator"));
        // And then it did its work.
        Assert.Contains(events, e => e.GetProperty("type").GetString() == "tool_call" && e.GetProperty("data").GetProperty("name").GetString() == "reportFinding");
    }

    [Fact]
    public async Task An_agent_nobody_said_a_person_is_there_for_has_no_way_to_ask()
    {
        var (client, _) = await host.NewUserAsync();

        // The same instructions, from a pipeline or a schedule: no "questions".
        var submitted = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "svc", model = "llama3.1", instructions = "Ask me first what matters." }));
        var jobId = submitted.GetProperty("id").GetString()!;

        Assert.Equal("Succeeded", (await EndAsync(client, jobId)).GetProperty("state").GetString());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>($"/questions?jobId={jobId}")).GetArrayLength());
        Assert.DoesNotContain(host.Tools.Requests.Last(r => r.Tools.Any(t => t.Name == "reportFinding")).Tools, t => t.Name == "askOperator");

        // And "questions" is for the agents that can ask.
        var wrong = await client.PostAsJsonAsync("/jobs", new { type = "scan", path = "svc", sca = false, questions = true });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("questions applies to sast, threat-model and investigate jobs.", (await Json(wrong)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_tool_that_may_change_something_outside_stops_the_run_and_a_no_means_it_is_not_called()
    {
        var (client, _) = await host.NewUserAsync();
        using var _ = host.Mcp.With(Tracker());
        var before = host.Mcp.Called.Count;

        var jobId = await StartResearchAsync(client);

        // The researcher wants to call the tracker's tool. The run stops and says exactly what it is about.
        var question = await QuestionAsync(client, jobId);
        var text = question.GetProperty("text").GetString()!;
        Assert.StartsWith("The agent wants to call \"create_ticket\" on the outside server \"tracker\".\n\nTool: mcp_tracker_create_ticket\nWith: {\"title\":", text);
        Assert.EndsWith("This sends something outside this machine and may change something in an outside system. It does not happen unless you allow it.", text);
        Assert.Equal(["Allow this once", "Allow for the rest of this run", "Do not allow"], question.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("label").GetString()!).ToArray());
        Assert.Equal(before, host.Mcp.Called.Count);   // nothing has been sent

        await AnswerAsync(client, question, "Do not allow", "we do not open tickets from reviews");

        Assert.Equal("Succeeded", (await EndAsync(client, jobId)).GetProperty("state").GetString());
        Assert.Equal(before, host.Mcp.Called.Count);   // and nothing ever was
        var refusal = ResultOf(await EventsAsync(client, jobId), "mcp_tracker_create_ticket");
        Assert.Equal("The operator did not allow this call and writes: \"we do not open tickets from reviews\". Do not try it again; do as they say, or go on without it.", refusal);
        Assert.Contains("tool.denied:denied", await AuditAsync(client));
    }

    [Fact]
    public async Task Allowed_once_the_call_is_made_and_always_allowing_the_kind_means_it_is_not_asked_about()
    {
        var (client, _) = await host.NewUserAsync();
        using var _ = host.Mcp.With(Tracker());
        var before = host.Mcp.Called.Count;

        // 1. Allowed for this one call.
        var first = await StartResearchAsync(client);
        await AnswerAsync(client, await QuestionAsync(client, first), "Allow this once");
        Assert.Equal("Succeeded", (await EndAsync(client, first)).GetProperty("state").GetString());
        Assert.Equal(before + 1, host.Mcp.Called.Count);
        Assert.Equal(("tracker", "create_ticket"), (host.Mcp.Called.Last().Server, host.Mcp.Called.Last().Tool));
        Assert.Contains("tool.allowed:allowed", await AuditAsync(client));

        // 2. The operator decides never to be asked about this kind of action again — the same "always allow" as everywhere.
        var permissions = await client.GetFromJsonAsync<JsonElement>("/settings/permissions");
        var allowed = permissions.GetProperty("allowedKinds").EnumerateArray().Select(k => k.GetString()!).Append("change-outside-systems").ToArray();
        Assert.True((await client.PutAsJsonAsync("/settings/permissions", new { allowedKinds = allowed })).IsSuccessStatusCode);

        var second = await StartResearchAsync(client);
        Assert.Equal("Succeeded", (await EndAsync(client, second)).GetProperty("state").GetString());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>($"/questions?jobId={second}")).GetArrayLength());
        Assert.Equal(before + 2, host.Mcp.Called.Count);
        var notes = (await EventsAsync(client, second)).Where(e => e.GetProperty("type").GetString() == "note").Select(e => e.GetProperty("data").GetProperty("message").GetString()!).ToList();
        Assert.Contains("Not asked about mcp_tracker_create_ticket: \"change-outside-systems\" is always allowed by the user permissions.", notes);
        Assert.Contains("tool.auto:allowed", await AuditAsync(client));
    }

    [Fact]
    public async Task The_gate_asks_again_for_each_call_unless_told_for_the_rest_of_the_run_and_silence_means_no()
    {
        var (client, _) = await host.NewUserAsync();
        var session = (await Json(await client.PostAsJsonAsync("/sessions", new { title = "x" }))).GetProperty("id").GetString()!;
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        var job = new Job { UserId = db.Sessions.Single(s => s.Id == session).UserId, Type = "sast", TargetPath = "svc" };
        var tool = new TicketTool();
        var input = JsonDocument.Parse("""{"title":"Rotate AKIAIOSFODNN7EXAMPLE"}""").RootElement;
        var reporter = new AnsweringReporter(
            new OperatorAnswer([OperatorGate.AllowOnce], null),
            new OperatorAnswer([OperatorGate.AllowForRun], null),
            new OperatorAnswer([], "use the staging tracker instead"));   // words alone, for another tool: a no with a reason
        var gate = new OperatorGate(job, db, scope.ServiceProvider.GetRequiredService<AuditLog>(), reporter);

        Assert.True((await gate.DecideAsync(tool, input, default)).Allowed);   // asked: once
        Assert.True((await gate.DecideAsync(tool, input, default)).Allowed);   // asked again: for the rest of the run
        Assert.True((await gate.DecideAsync(tool, input, default)).Allowed);   // not asked
        Assert.True((await gate.DecideAsync(tool, input, default)).Allowed);   // not asked
        Assert.Equal(2, reporter.Asked.Count);
        // What the operator is shown never carries a credential the model happened to quote.
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", reporter.Asked[0].Text);
        Assert.Contains("The agent wants to open a ticket in the tracker.", reporter.Asked[0].Text);

        // "For the rest of the run" is for that tool, not for every tool of the kind.
        var other = new RenamedTool(tool, "deleteTicket");
        var said = await gate.DecideAsync(other, input, default);
        Assert.False(said.Allowed);
        Assert.Contains("writes: \"use the staging tracker instead\"", said.Reason);
        // And when nobody answers at all, the call is not made.
        var silence = await gate.DecideAsync(other, input, default);
        Assert.False(silence.Allowed);
        Assert.StartsWith("Nobody was there to allow this call, so it was not made.", silence.Reason);
    }

    private sealed class RenamedTool(IAgentTool inner, string name) : IAgentTool
    {
        public ToolDefinition Definition { get; } = inner.Definition with { Name = name };
        public ToolEffects Effects => inner.Effects;
        public string? AskFirst => inner.AskFirst;
        public string Describe(JsonElement input) => "delete a ticket in the tracker";
        public Task<string> RunAsync(JsonElement input, CancellationToken ct) => inner.RunAsync(input, ct);
    }
}

public class ToolEffectsTests
{
    [Theory]
    [InlineData(ToolEffects.None, "is marked as needing your consent")]
    [InlineData(ToolEffects.SendsOut, "sends something outside this machine")]
    [InlineData(ToolEffects.SendsOut | ToolEffects.ChangesOutside, "sends something outside this machine and may change something in an outside system")]
    [InlineData(ToolEffects.WritesFiles | ToolEffects.LaunchesProcess | ToolEffects.Records, "creates or changes files, starts a process and keeps something in this system")]
    public void The_operator_is_told_in_words_what_a_call_would_do(ToolEffects effects, string words)
    {
        Assert.Equal(words, OperatorGate.InWords(effects));
    }

    [Fact]
    public void An_outside_servers_tool_asks_first_exactly_when_the_server_does_not_call_it_read_only()
    {
        var server = new McpServerConfig("tracker", new Uri("https://tracker.example/mcp"), new Dictionary<string, string>(), ["search", "create_ticket", "unannotated"]);
        var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement;

        var (exposed, _) = McpExposure.Decide(server,
        [
            new McpToolInfo("search", "Searches.", schema, ReadOnly: true, Destructive: null),
            new McpToolInfo("create_ticket", "Opens a ticket.", schema, ReadOnly: false, Destructive: false),
            new McpToolInfo("unannotated", "Says nothing about itself.", schema, ReadOnly: null, Destructive: null),
        ]);

        Assert.Equal([("search", false), ("create_ticket", true), ("unannotated", true)], exposed.Select(t => (t.Tool, t.ChangesThings)).ToArray());
        var servers = new FakeMcp();
        IAgentTool reading = new McpAgentTool(servers, exposed[0], new EgressGuard(), new RecordingReporter());
        IAgentTool changing = new McpAgentTool(servers, exposed[1], new EgressGuard(), new RecordingReporter());
        Assert.Equal((null, ToolEffects.SendsOut), (reading.AskFirst, reading.Effects));
        Assert.Equal((ActionKinds.ChangeOutsideSystems, ToolEffects.SendsOut | ToolEffects.ChangesOutside), (changing.AskFirst, changing.Effects));
        Assert.Equal("call \"create_ticket\" on the outside server \"tracker\"", changing.Describe(default));
        // The model is told, too, that this one will be asked about.
        Assert.Contains("the operator is asked before each call", exposed[1].Description);
        Assert.DoesNotContain("the operator is asked", exposed[0].Description);
    }
}
