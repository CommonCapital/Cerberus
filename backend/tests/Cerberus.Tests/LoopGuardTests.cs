using System.Text.Json;
using Cerberus.Api.Agents;
using Cerberus.Core.Models;

namespace Cerberus.Tests;

public class LoopGuardTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void The_same_call_runs_twice_and_is_refused_from_then_on()
    {
        var guard = new LoopGuard(maxToolCalls: 50);

        Assert.Null(guard.Refuse("readFile", J("""{"path":"a.py"}""")));
        Assert.Null(guard.Refuse("readFile", J("""{"path":"a.py"}""")));
        var third = guard.Refuse("readFile", J("""{"path":"a.py"}"""));

        Assert.Contains("already made this exact call 2 times", third);
        Assert.NotNull(guard.Refuse("readFile", J("""{"path":"a.py"}""")));
        // Something else is still allowed, and one repeated call does not end the run.
        Assert.Null(guard.Refuse("readFile", J("""{"path":"b.py"}""")));
        Assert.Null(guard.Refuse("grep", J("""{"path":"a.py"}""")));
        Assert.Null(guard.StopReason);
    }

    [Fact]
    public void A_call_is_the_same_call_however_its_arguments_are_written()
    {
        var guard = new LoopGuard(maxToolCalls: 50);

        Assert.Null(guard.Refuse("grep", J("""{"pattern":"eval","path":"src","opts":{"a":1,"b":[1,2]}}""")));
        Assert.Null(guard.Refuse("grep", J("""{ "path": "src",  "opts": {"b":[1,2], "a":1}, "pattern": "eval" }""")));
        Assert.NotNull(guard.Refuse("grep", J("""{"opts":{"a":1,"b":[1,2]},"path":"src","pattern":"eval"}""")));

        // Order inside a list is part of what was asked; a different value is a different call.
        Assert.NotEqual(LoopGuard.Canonical(J("""{"b":[1,2]}""")), LoopGuard.Canonical(J("""{"b":[2,1]}""")));
        Assert.NotEqual(LoopGuard.Canonical(J("""{"offset":1}""")), LoopGuard.Canonical(J("""{"offset":2001}""")));
        Assert.Equal("""{"a":"x y","b":null}""", LoopGuard.Canonical(J("""{ "b" : null, "a" : "x y" }""")));
    }

    [Fact]
    public void A_run_has_a_budget_of_tool_calls()
    {
        var guard = new LoopGuard(maxToolCalls: 3);

        for (var i = 0; i < 3; i++) Assert.Null(guard.Refuse("readFile", J($$"""{"path":"f{{i}}.py"}""")));
        Assert.Null(guard.StopReason);

        Assert.Contains("budget of tool calls is used up", guard.Refuse("readFile", J("""{"path":"one-more.py"}""")));
        Assert.Equal("it used up its budget of 3 tool calls", guard.StopReason);
    }

    [Fact]
    public void Steps_that_get_nowhere_end_the_run_and_a_useful_step_starts_the_count_again()
    {
        var guard = new LoopGuard(maxToolCalls: 50);

        guard.StepDone(useful: false);
        guard.StepDone(useful: false);
        guard.StepDone(useful: true);
        guard.StepDone(useful: false);
        guard.StepDone(useful: false);
        Assert.Null(guard.StopReason);

        guard.StepDone(useful: false);
        Assert.Equal("its last 3 steps got nowhere (tool calls that were repeated or failed)", guard.StopReason);
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(4, 20)]
    [InlineData(20, 100)]
    [InlineData(40, 200)]
    public void The_budget_follows_the_step_limit(int maxSteps, int budget)
    {
        Assert.Equal(budget, LoopGuard.BudgetFor(maxSteps));
    }
}

public class GuardedAgentLoopTests
{
    private sealed class Files : IAgentTool
    {
        public List<string> Read { get; } = [];
        public ToolDefinition Definition { get; } = new("readFile", "Reads.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public Task<string> RunAsync(JsonElement input, CancellationToken ct)
        {
            var path = input.GetProperty("path").GetString()!;
            if (path.StartsWith("missing", StringComparison.Ordinal)) throw new AgentToolException($"There is no file {path}.");
            Read.Add(path);
            return Task.FromResult($"contents of {path}");
        }
    }

    private static ChatEvent[] Call(string id, string path) =>
        [new ToolCallComplete(id, "readFile", $$"""{"path":"{{path}}"}"""), new StepFinished("tool_calls")];

    private static ChatEvent[] Say(string text) => [new TextDelta(text), new StepFinished("stop")];

    private static async Task<(AgentOutcome Outcome, RecordingReporter Reporter, Files Tool)> RunAsync(PlaybackProvider model, int maxSteps = 10, LoopGuard? guard = null)
    {
        var reporter = new RecordingReporter();
        var tool = new Files();
        var outcome = await new AgentLoop(new SingleProviderResolver(model))
            .RunAsync("echo", new ModelCredentials(), "system", "task", [tool], maxSteps, reporter, default, guard: guard);
        return (outcome, reporter, tool);
    }

    [Fact]
    public async Task A_call_made_for_the_third_time_is_not_run_and_the_model_is_told_why()
    {
        var model = new PlaybackProvider(Call("c1", "a.py"), Call("c2", "a.py"), Call("c3", "a.py"), Say("It calls eval."));

        var (outcome, reporter, tool) = await RunAsync(model);

        Assert.Equal(["a.py", "a.py"], tool.Read);   // the third was not run
        Assert.Equal(("It calls eval.", 4, 3, false), (outcome.FinalText, outcome.Steps, outcome.ToolCalls, outcome.StoppedEarly));
        // The model got the refusal as that call's result, marked as failed.
        var result = model.Requests[3].Turns[^1].Parts.OfType<ToolResultPart>().Single();
        Assert.True(result.IsError);
        Assert.Contains("already made this exact call 2 times", result.Content);
        // And the operator sees the call and what became of it.
        Assert.Equal(3, reporter.Events.Count(e => e.Type == "tool_call"));
        Assert.Contains(reporter.Events, e => e.Type == "tool_result" && e.Json.Contains("already made this exact call"));
    }

    [Fact]
    public async Task An_agent_stuck_on_the_same_call_is_stopped_long_before_its_step_limit_and_still_sums_up()
    {
        // Asks for the same file for ever (the provider repeats its last step).
        var model = new PlaybackProvider(Call("c", "a.py"));

        var (outcome, _, tool) = await RunAsync(model, maxSteps: 40);

        Assert.Equal(["a.py", "a.py"], tool.Read);
        // Two real reads, then three steps of refusals: stopped at step 5 of 40.
        Assert.Equal(5, outcome.Steps);
        Assert.False(outcome.HitStepLimit);
        Assert.True(outcome.StoppedEarly);
        Assert.Equal("its last 3 steps got nowhere (tool calls that were repeated or failed)", outcome.StoppedBecause);
        Assert.Equal("The agent was stopped: its last 3 steps got nowhere (tool calls that were repeated or failed); the review may be incomplete.",
            outcome.StopNote(40, "the review may be incomplete"));

        // One last request: no tools on offer, told why it was stopped.
        Assert.Equal(6, model.Requests.Count);
        Assert.Empty(model.Requests[5].Tools);
        Assert.StartsWith("You have been stopped: its last 3 steps got nowhere", ChatEvents.TextOf(model.Requests[5].Turns[^1]));
    }

    [Fact]
    public async Task Calls_that_keep_failing_end_the_run_too()
    {
        var model = new PlaybackProvider(Call("c1", "missing-1"), Call("c2", "missing-2"), Call("c3", "missing-3"), Say("never asked"));

        var (outcome, _, _) = await RunAsync(model);

        Assert.Equal(3, outcome.Steps);
        Assert.Contains("got nowhere", outcome.StoppedBecause);
    }

    [Fact]
    public async Task One_step_that_works_is_enough_to_carry_on()
    {
        var model = new PlaybackProvider(
            Call("c1", "missing-1"), Call("c2", "missing-2"), Call("c3", "a.py"), Call("c4", "missing-3"), Call("c5", "missing-4"), Say("Found it in a.py."));

        var (outcome, _, _) = await RunAsync(model);

        Assert.Equal(("Found it in a.py.", false), (outcome.FinalText, outcome.StoppedEarly));
    }

    [Fact]
    public async Task When_the_tool_budget_is_spent_the_rest_of_the_step_is_not_run_and_the_run_ends_with_a_summary()
    {
        ChatEvent[] three = [
            new ToolCallComplete("c1", "readFile", """{"path":"a.py"}"""),
            new ToolCallComplete("c2", "readFile", """{"path":"b.py"}"""),
            new ToolCallComplete("c3", "readFile", """{"path":"c.py"}"""),
            new StepFinished("tool_calls")];
        var model = new PlaybackProvider(three, Say("Read two of three files."));

        var (outcome, _, tool) = await RunAsync(model, guard: new LoopGuard(maxToolCalls: 2));

        Assert.Equal(["a.py", "b.py"], tool.Read);
        Assert.Equal("it used up its budget of 2 tool calls", outcome.StoppedBecause);
        Assert.Equal(("Read two of three files.", 1, 3), (outcome.FinalText, outcome.Steps, outcome.ToolCalls));
        Assert.Empty(model.Requests[1].Tools);
        // Every call of the step has a result, as providers require — the refused one says why.
        var results = model.Requests[1].Turns[^2].Parts.OfType<ToolResultPart>().ToList();
        Assert.Equal(["c1", "c2", "c3"], results.Select(r => r.ToolCallId).ToArray());
        Assert.Contains("budget of tool calls is used up", results[2].Content);
    }

    [Fact]
    public void A_run_that_ended_by_itself_has_nothing_to_say_about_being_stopped()
    {
        var finished = new AgentOutcome("Done.", 3, 4, HitStepLimit: false, null, null);
        var atLimit = new AgentOutcome("Partly.", 20, 30, HitStepLimit: true, null, null);

        Assert.Null(finished.StopNote(20, "the review may be incomplete"));
        Assert.False(finished.StoppedEarly);
        Assert.Equal("The agent was stopped at its limit of 20 steps; the review may be incomplete.", atLimit.StopNote(20, "the review may be incomplete"));
        Assert.True(atLimit.StoppedEarly);
    }
}
