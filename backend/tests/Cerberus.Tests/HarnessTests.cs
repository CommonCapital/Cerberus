using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api.Agents;
using Cerberus.Api.Audit;
using Cerberus.Api.Search;
using Cerberus.Api.Settings;
using Cerberus.Api.Web;
using Cerberus.Authorization;
using Cerberus.Core.Models;
using Cerberus.Data;
using Cerberus.Jobs;
using Cerberus.Scanning;
using Cerberus.Skills;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cerberus.Tests;

/// <summary>
/// The harness, exercised with agents that exist only in this file: a description and a tool each, and no
/// runner of their own — which is what adding an agent is meant to take.
/// </summary>
[Collection("api")]
public class AgentHarnessTests(ApiHost host)
{
    /// <summary>An agent that counts the licence headers it is shown. Everything about it is these few lines.</summary>
    private static readonly AgentSpec Licences = new(
        "licences", StepId: "licences", System: "You check licence headers. Record each file's licence.", StepLimit: 4,
        IfCutShort: "some files may be unchecked", AgentOffers.Code | AgentOffers.Notes | AgentOffers.Skills);

    private sealed class RecordLicenceTool(List<string> recorded) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new("recordLicence", "Records a file's licence.",
            JsonDocument.Parse("""{"type":"object","properties":{"file":{"type":"string"},"licence":{"type":"string"}},"required":["file","licence"]}""").RootElement.Clone());

        public Task<string> RunAsync(JsonElement input, CancellationToken ct)
        {
            recorded.Add($"{input.GetProperty("file").GetString()}: {input.GetProperty("licence").GetString()}");
            return Task.FromResult("Recorded.");
        }
    }

    private sealed class LicenceCheck(AgentContext context, IReadOnlyList<IAgentTool>? tools = null) : IAgentWork
    {
        public List<string> Recorded { get; } = [];
        public bool Ended { get; private set; }
        private IReadOnlyList<IAgentTool>? _tools = tools;

        public string Title => $"Check the licences with {context.Model}";
        public string Assignment => $"Check the licence headers under \"{context.TargetPath}\" in at most {context.MaxSteps} steps.";
        public IReadOnlyList<IAgentTool> Tools => _tools ??= [new RecordLicenceTool(Recorded)];

        public Task EndedAsync(AgentOutcome outcome)
        {
            Ended = true;
            return Task.CompletedTask;
        }

        public void Fill(AgentRun run, AgentOutcome? outcome)
        {
            run.Findings = JsonSerializer.Serialize(Recorded);
            run.FindingCount = Recorded.Count;
        }

        public AuditLine Audit(AgentRun? run, AgentOutcome? outcome) => new("licences.run", outcome is { StoppedEarly: false },
            $"Licence check {(run is null ? "(not kept)" : run.Id)}: {Recorded.Count} recorded; sent to {context.SentTo}.");
    }

    private static ChatEvent[] Call(string id, string tool, string json) => [new ToolCallComplete(id, tool, json), new StepFinished("tool_calls")];
    private static ChatEvent[] Say(string text) => [new TextDelta(text), new StepFinished("stop")];

    private sealed class Rig(IServiceScope scope, Job job, PlaybackProvider model) : IDisposable
    {
        public Job Job { get; } = job;
        public PlaybackProvider Model { get; } = model;
        public CerberusDbContext Db { get; } = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        public OperatorInbox Inbox { get; } = scope.ServiceProvider.GetRequiredService<OperatorInbox>();

        /// <summary>The real harness with every real part of the service — only the model is scripted.</summary>
        public AgentHarness Harness { get; } = new(
            new AgentLoop(new SingleProviderResolver(model)),
            scope.ServiceProvider.GetRequiredService<ApiKeyService>(), scope.ServiceProvider.GetRequiredService<CerberusDbContext>(),
            scope.ServiceProvider.GetRequiredService<AuditLog>(), scope.ServiceProvider.GetRequiredService<ScanOptions>(),
            scope.ServiceProvider.GetRequiredService<SkillLibrary>(), scope.ServiceProvider.GetRequiredService<OperatorInbox>(),
            scope.ServiceProvider.GetRequiredService<CodeSearch>(), scope.ServiceProvider.GetRequiredService<ResearchTools>());

        public AgentRequest Request(int maxSteps = 4, bool research = false, bool questions = false, SpecialistBrief? brief = null) =>
            new(Job, "echo", maxSteps, "svc", research, questions, brief);

        public Task<List<string>> AuditAsync() => Db.AuditEvents.AsNoTracking().Where(e => e.UserId == Job.UserId).OrderBy(e => e.Sequence).Select(e => $"{e.Action}|{e.Outcome}|{e.Detail}").ToListAsync();

        public void Dispose() => scope.Dispose();
    }

    private async Task<Rig> RigAsync(params ChatEvent[][] script)
    {
        var (client, _) = await host.NewUserAsync();
        var session = (await (await client.PostAsJsonAsync("/sessions", new { title = "x" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var scope = host.Services.CreateScope();
        var userId = scope.ServiceProvider.GetRequiredService<CerberusDbContext>().Sessions.Single(s => s.Id == session).UserId;
        return new Rig(scope, new Job { UserId = userId, Type = "licences", TargetPath = "svc" }, new PlaybackProvider(script));
    }

    [Fact]
    public async Task A_new_agent_is_a_description_and_its_tools_and_gets_everything_else_from_the_harness()
    {
        using var rig = await RigAsync(
            Call("c1", "readFile", """{"path":"loader.py"}"""),
            Call("c2", "recordLicence", """{"file":"loader.py","licence":"none"}"""),
            Say("One file, no licence header. The key AKIAIOSFODNN7EXAMPLE was in a comment."));
        var reporter = new RecordingReporter();
        LicenceCheck? work = null;

        var result = await rig.Harness.RunAsync(Licences, rig.Request(), context => work = new LicenceCheck(context), reporter, default);

        // What the model was given: the description's instructions, the task, and the tools the description takes —
        // the code tools, the agent's own, notes and skills; not research, not questions, which it does not take.
        var first = rig.Model.Requests[0];
        Assert.StartsWith("You check licence headers.", first.System);
        Assert.Equal("Check the licence headers under \"svc\" in at most 4 steps.", ChatEvents.TextOf(first.Turns[0]));
        var offered = first.Tools.Select(t => t.Name).ToList();
        Assert.Contains("readFile", offered);
        Assert.Equal(["recordLicence", "remember", "searchSkills", "loadSkill"], offered.Skip(offered.IndexOf("recordLicence")).ToArray());
        Assert.DoesNotContain(offered, t => t is "research" or "askOperator" or "searchCode");

        // On the run: its step, from start to finish.
        var steps = reporter.Events.Where(e => e.Type == RunEventTypes.ToolCall).ToList();
        Assert.Equal(2, steps.Count);
        Assert.True(work!.Ended);

        // Kept: a run under the agent's name, with what it recorded and its words masked.
        var run = result.Run!;
        Assert.Equal(("licences", true, 1, 3, 2), (run.Agent, run.Complete, run.FindingCount, run.Steps, run.ToolCalls));
        Assert.Equal("""["loader.py: none"]""", run.Findings);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", run.Summary);
        Assert.Equal(run.Id, (await rig.Db.AgentRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).Id);
        Assert.Contains($"licences.run|ok|Licence check {run.Id}: 1 recorded; sent to a local model.", await rig.AuditAsync());
    }

    [Fact]
    public async Task The_step_limit_is_the_descriptions_whatever_a_run_asks_for()
    {
        // The model would go on for ever; the description allows four steps.
        using var rig = await RigAsync(Call("c", "listDirectory", """{"path":"."}"""));
        var reporter = new RecordingReporter();

        var result = await rig.Harness.RunAsync(Licences, rig.Request(maxSteps: 500), context => new LicenceCheck(context), reporter, default);

        Assert.True(result.Outcome.StoppedEarly);
        Assert.False(result.Run!.Complete);
        Assert.InRange(result.Outcome.Steps, 1, 4);
        Assert.Contains("at most 4 steps", ChatEvents.TextOf(rig.Model.Requests[0].Turns[0]));
        Assert.Contains(reporter.Events, e => e.Type == RunEventTypes.Note && e.Json.Contains("some files may be unchecked"));
        Assert.Contains(await rig.AuditAsync(), a => a.StartsWith("licences.run|error|"));
    }

    [Fact]
    public async Task What_a_description_does_not_take_a_run_cannot_ask_for_and_what_it_takes_still_needs_the_run_to_ask()
    {
        var plain = Licences with { Offers = AgentOffers.None };
        var asking = Licences with { Offers = AgentOffers.Code | AgentOffers.Questions };

        using var a = await RigAsync(Say("Nothing to do."));
        await a.Harness.RunAsync(plain, a.Request(research: true, questions: true), context => new LicenceCheck(context), new RecordingReporter(), default);
        Assert.Equal(["recordLicence"], a.Model.Requests[0].Tools.Select(t => t.Name).ToArray());   // only its own tool: no code tools, no questions

        using var b = await RigAsync(Say("Nothing to do."));
        await b.Harness.RunAsync(asking, b.Request(questions: false), context => new LicenceCheck(context), new RecordingReporter(), default);
        Assert.DoesNotContain(b.Model.Requests[0].Tools, t => t.Name == "askOperator");            // nobody said a person is there

        using var c = await RigAsync(Say("Nothing to do."));
        await c.Harness.RunAsync(asking, c.Request(questions: true), context => new LicenceCheck(context), new RecordingReporter(), default);
        Assert.Contains(c.Model.Requests[0].Tools, t => t.Name == "askOperator");
        Assert.Contains("you may ask the operator with askOperator", ChatEvents.TextOf(c.Model.Requests[0].Turns[0]));   // and it is told how
    }

    [Fact]
    public async Task An_agent_that_works_for_a_lead_keeps_its_leads_name_and_cannot_reach_the_operator()
    {
        using var rig = await RigAsync(Say("Looked."));
        var brief = new SpecialistBrief("specialist:licences", "You are sent by a lead to look at the vendored code only.", "the-leads-run");
        var asking = Licences with { Offers = AgentOffers.Code | AgentOffers.Questions };
        var posted = false;
        // While it runs, a message from the operator is not this agent's to take: the lead's inbox is.
        rig.Model.BeforeReply = () => posted = rig.Inbox.TryPost(rig.Job.Id, "are you there?");

        var result = await rig.Harness.RunAsync(asking, rig.Request(questions: true, brief: brief), context => new LicenceCheck(context), new RecordingReporter(), default);

        Assert.Equal(("specialist:licences", "the-leads-run"), (result.Run!.Agent, result.Run.ParentRunId));
        Assert.EndsWith("You are sent by a lead to look at the vendored code only.", rig.Model.Requests[0].System!.Split("\n\n")[1]);
        Assert.DoesNotContain(rig.Model.Requests[0].Tools, t => t.Name == "askOperator");   // even though the run and the description allow questions
        Assert.False(posted);
    }

    [Fact]
    public async Task A_run_that_breaks_off_keeps_what_it_had_and_says_so()
    {
        using var rig = await RigAsync(
            Call("c1", "recordLicence", """{"file":"loader.py","licence":"MIT"}"""),
            [new TextDelta("…"), new StepFinished("stop")]);
        rig.Model.FailAt = 2;   // the provider goes away on the second request
        LicenceCheck? work = null;

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            rig.Harness.RunAsync(Licences, rig.Request(), context => work = new LicenceCheck(context), new RecordingReporter(), default));

        var kept = await rig.Db.AgentRuns.AsNoTracking().SingleAsync(r => r.UserId == rig.Job.UserId);
        Assert.Equal(("licences", false, 1), (kept.Agent, kept.Complete, kept.FindingCount));
        Assert.False(work!.Ended);   // it did not end in the ordinary way
        Assert.Contains(await rig.AuditAsync(), a => a.StartsWith("licences.run|error|"));
    }

    [Fact]
    public async Task An_agent_whose_results_live_elsewhere_keeps_no_run_but_is_still_in_the_audit_log()
    {
        using var rig = await RigAsync(Say("Done."));

        var result = await rig.Harness.RunAsync(Licences with { Stored = false }, rig.Request(), context => new LicenceCheck(context), new RecordingReporter(), default);

        Assert.Null(result.Run);
        Assert.False(await rig.Db.AgentRuns.AnyAsync(r => r.UserId == rig.Job.UserId));
        Assert.Contains("licences.run|ok|Licence check (not kept): 0 recorded; sent to a local model.", await rig.AuditAsync());
    }

    [Fact]
    public async Task Any_agents_tool_that_needs_consent_goes_to_the_operator_without_the_agent_arranging_it()
    {
        using var rig = await RigAsync(
            Call("c1", "createTicket", """{"title":"Add licence headers"}"""),
            Call("c2", "createTicket", """{"title":"Add licence headers, please"}"""),
            Say("One ticket."));
        var tickets = new TicketTool();
        var reporter = new AnsweringReporter(new OperatorAnswer([OperatorGate.Deny], "not from this agent"), new OperatorAnswer([OperatorGate.AllowOnce], null));

        await rig.Harness.RunAsync(Licences, rig.Request(), context => new LicenceCheck(context, [tickets]), reporter, default);

        // The agent's description says nothing about consent; the tool does, and the harness's gate asked.
        Assert.Equal(2, reporter.Asked.Count);
        Assert.Contains("The agent wants to open a ticket in the tracker.", reporter.Asked[0].Text);
        Assert.Equal(["Add licence headers, please"], tickets.Made);
        var audit = await rig.AuditAsync();
        Assert.Contains(audit, a => a.StartsWith("tool.denied|denied|"));
        Assert.Contains(audit, a => a.StartsWith("tool.allowed|allowed|"));
    }

    [Fact]
    public void The_agents_of_the_product_are_descriptions_too()
    {
        // The four that have a run of their own: one description each, no runner logic left in them.
        Assert.Equal(("sast", "review", 50, true), (SastAgent.Spec.Name, SastAgent.Spec.StepId, SastAgent.Spec.StepLimit, SastAgent.Spec.Stored));
        Assert.Equal(("threat-model", "threat-model", true), (ThreatModelAgent.Spec.Name, ThreatModelAgent.Spec.StepId, ThreatModelAgent.Spec.Stored));
        Assert.Equal(("evidence", false), (EvidenceAgent.Spec.Name, EvidenceAgent.Spec.Stored));   // its results are the assessment's suggestions
        Assert.Equal(("lead", "lead", Team.LeadSteps, "lead"), (Team.Spec.Name, Team.Spec.StepId, Team.Spec.StepLimit, Team.Spec.Called));

        // Who may use the internet or ask the operator is the description's to say.
        Assert.True(SastAgent.Spec.Offers.HasFlag(AgentOffers.Research | AgentOffers.Questions));
        Assert.True(ThreatModelAgent.Spec.Offers.HasFlag(AgentOffers.Research | AgentOffers.Questions));
        Assert.False(EvidenceAgent.Spec.Offers.HasFlag(AgentOffers.Research) || EvidenceAgent.Spec.Offers.HasFlag(AgentOffers.Questions));
        Assert.Equal(AgentOffers.Code | AgentOffers.Questions, Team.Spec.Offers);   // the lead looks and asks; it records nothing itself
    }
}
