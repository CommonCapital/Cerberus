using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerberus.Api.Agents;
using Cerberus.Api.Chat;
using Cerberus.Api.Controls;
using Cerberus.Api.Security;
using Cerberus.Core.Models;

namespace Cerberus.Tests;

public class DisabledToolsTests
{
    [Fact]
    public void A_name_that_is_not_a_group_stops_the_service_and_the_message_lists_the_real_ones()
    {
        string? Env(string value) => value;

        Assert.Equal(
            ["CERBERUS_DISABLED_TOOLS names a tool group that does not exist: brwoser. The groups are: web, browser, mcp, shell, file-edits, helpers, notes, skills, questions."],
            ServiceConfig.Validate(name => name == DisabledTools.Variable ? Env("web, brwoser") : null));
        Assert.Contains("tool groups that do not exist: everything, bash.",
            ServiceConfig.Validate(name => name == DisabledTools.Variable ? "everything,bash" : null).Single());
        Assert.Empty(ServiceConfig.Validate(name => name == DisabledTools.Variable ? " Web , SHELL,,mcp " : null));
        Assert.Throws<InvalidOperationException>(() => DisabledTools.Parse("nope"));
    }

    [Fact]
    public void Groups_are_read_in_any_case_and_said_in_a_fixed_order()
    {
        var off = DisabledTools.Parse(" Shell, web ,questions");

        Assert.Equal(["web", "shell", "questions"], off.Off);
        Assert.True(off.IsOff("shell"));
        Assert.False(off.IsOff("mcp"));
        Assert.Throws<ArgumentException>(() => off.IsOff("bash"));   // a typo in code is caught too
        Assert.Empty(DisabledTools.Parse(null).Off);
        Assert.Empty(DisabledTools.None.Off);
    }

    [Fact]
    public void The_chat_agent_is_not_offered_what_is_switched_off()
    {
        var all = ChatTools.For(ChatTools.ModeBuild, delegation: true).Select(t => t.Name).ToList();
        Assert.Contains("bash", all);

        var allowed = DisabledTools.Parse("shell,file-edits,helpers,skills").Allow(ChatTools.For(ChatTools.ModeBuild, delegation: true)).Select(t => t.Name).ToList();

        Assert.Equal(["readFile", "listDirectory", "glob", "grep", "updateTasks"], allowed);
        // Reading is not a group: an installation that wants no chat at all does not offer chat.
        Assert.Equal(all, DisabledTools.None.Allow(ChatTools.For(ChatTools.ModeBuild, delegation: true)).Select(t => t.Name).ToList());
    }

    [Fact]
    public void An_agent_keeps_what_its_description_offers_less_what_is_switched_off()
    {
        var everything = AgentOffers.Code | AgentOffers.CodeSearch | AgentOffers.Research | AgentOffers.Questions | AgentOffers.Notes | AgentOffers.Skills;

        Assert.Equal(AgentOffers.Code | AgentOffers.CodeSearch | AgentOffers.Research, DisabledTools.Parse("notes,skills,questions").Allow(everything));
        Assert.Equal(everything, DisabledTools.Parse("shell,web").Allow(everything));   // research is switched off where the web and servers are (see start-up)
        Assert.Equal(AgentOffers.Code, DisabledTools.Parse("notes").Allow(AgentOffers.Code));
    }
}

public class AgentTimeLimitTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SlowTool(Clock clock) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new("readFile", "Reads.", JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone());

        public Task<string> RunAsync(JsonElement input, CancellationToken ct)
        {
            clock.Now += TimeSpan.FromMinutes(2);   // every call takes two minutes
            return Task.FromResult("contents");
        }
    }

    private static ChatEvent[] Call(string id, string path) => [new ToolCallComplete(id, "readFile", $$"""{"path":"{{path}}"}"""), new StepFinished("tool_calls")];

    [Fact]
    public async Task Past_its_time_limit_an_agent_is_stopped_after_its_step_and_still_gives_its_summary()
    {
        var clock = new Clock();
        // The fourth request is the closing one: it gets the account.
        var model = new PlaybackProvider(Call("c1", "a.py"), Call("c2", "b.py"), Call("c3", "c.py"),
            [new TextDelta("Read a.py, b.py and c.py; did not get to the rest."), new StepFinished("stop")]);
        var guard = new LoopGuard(LoopGuard.BudgetFor(10), TimeSpan.FromMinutes(5), clock);

        var outcome = await new AgentLoop(new SingleProviderResolver(model))
            .RunAsync("echo", new ModelCredentials(), "system", "task", [new SlowTool(clock)], 10, new RecordingReporter(), default, guard: guard);

        // Two, four, six minutes: stopped after the third step, which crossed the limit — not in the middle of it.
        Assert.Equal((3, 3), (outcome.Steps, outcome.ToolCalls));
        Assert.Equal("it reached its time limit of 5 minutes", outcome.StoppedBecause);
        Assert.Equal("The agent was stopped: it reached its time limit of 5 minutes; the review may be incomplete.", outcome.StopNote(10, "the review may be incomplete"));
        // And it was asked for its account, without tools, and gave it.
        var closing = model.Requests[^1];
        Assert.Empty(closing.Tools);
        Assert.StartsWith("You have been stopped: it reached its time limit of 5 minutes.", ChatEvents.TextOf(closing.Turns[^1]));
        Assert.Equal("Read a.py, b.py and c.py; did not get to the rest.", outcome.FinalText);
    }

    [Fact]
    public async Task Without_a_limit_the_clock_plays_no_part()
    {
        var clock = new Clock();
        var model = new PlaybackProvider(Call("c1", "a.py"), Call("c2", "b.py"), Call("c3", "c.py"), [new TextDelta("Done."), new StepFinished("stop")]);

        var outcome = await new AgentLoop(new SingleProviderResolver(model))
            .RunAsync("echo", new ModelCredentials(), "system", "task", [new SlowTool(clock)], 10, new RecordingReporter(), default,
                guard: new LoopGuard(LoopGuard.BudgetFor(10), null, clock));

        Assert.Null(outcome.StoppedBecause);
        Assert.Equal("Done.", outcome.FinalText);
    }

    [Theory]
    [InlineData(300, "5 minutes")]
    [InlineData(60, "1 minute")]
    [InlineData(90, "90 seconds")]
    [InlineData(2.5, "2.5 seconds")]
    [InlineData(0.25, "250 milliseconds")]
    [InlineData(0.0001, "1 millisecond")]
    public async Task The_limit_is_said_the_way_a_person_would_say_it(double seconds, string said)
    {
        var clock = new Clock();
        var guard = new LoopGuard(20, TimeSpan.FromSeconds(seconds), clock);
        clock.Now += TimeSpan.FromSeconds(seconds);
        guard.StepDone(useful: true);
        Assert.Equal($"it reached its time limit of {said}", guard.StopReason);
        await Task.CompletedTask;
    }

    [Fact]
    public void A_time_limit_that_is_not_a_number_of_seconds_stops_the_service()
    {
        Assert.Equal(["CERBERUS_AGENT_TIME_LIMIT_SECONDS must be a number of seconds greater than zero (a decimal point, not a comma)."],
            ServiceConfig.Validate(name => name == "CERBERUS_AGENT_TIME_LIMIT_SECONDS" ? "5,5" : null));
        Assert.Empty(ServiceConfig.Validate(name => name == "CERBERUS_AGENT_TIME_LIMIT_SECONDS" ? "900" : null));
    }
}

public class ModelRolesTests
{
    private static readonly ModelCredentials NoKeys = new();
    private static readonly ModelCredentials AnthropicKey = new(AnthropicApiKey: "made-up");

    [Fact]
    public void Without_a_fast_model_the_conversations_own_does_everything()
    {
        var roles = new ModelRoles(null);

        Assert.Equal(("claude-opus-5-5", "no fast model is configured"), roles.Decide("claude-opus-5-5", AnthropicKey, summary: false));
        Assert.Null(new ModelRoles("  ").FastModel);
    }

    [Fact]
    public void A_fast_model_of_the_same_provider_is_used_where_there_is_a_key_for_it()
    {
        var roles = new ModelRoles("claude-haiku-5-5");

        Assert.Equal("claude-haiku-5-5", roles.ForTitle("claude-opus-5-5", AnthropicKey));
        Assert.Equal(("claude-opus-5-5", "there is no key for Anthropic"), roles.Decide("claude-opus-5-5", NoKeys, summary: false));
    }

    [Fact]
    public void A_fast_model_never_takes_a_conversation_somewhere_it_does_not_already_go()
    {
        var cloud = new ModelRoles("claude-haiku-5-5");

        // A conversation with a local model stays on this machine, title and all.
        Assert.Equal(("llama3.1", "the fast model would send the conversation to Anthropic, where it does not go now"),
            cloud.Decide("llama3.1", AnthropicKey, summary: false));
        // Nor does a conversation with one provider go to another for its summary.
        Assert.Equal("deepseek-chat", cloud.ForSummary("deepseek-chat", new ModelCredentials(AnthropicApiKey: "made-up", DeepSeekApiKey: "made-up")));
    }

    [Fact]
    public void A_local_fast_model_may_always_write_a_title_but_a_summary_only_if_it_can_read_as_much()
    {
        var local = new ModelRoles("ollama:llama3.2");

        Assert.Equal("ollama:llama3.2", local.ForTitle("claude-opus-5-5", AnthropicKey));
        Assert.Equal("ollama:llama3.2", local.ForTitle("llama3.1", NoKeys));
        Assert.Equal(("claude-opus-5-5", "the fast model reads less than the conversation's own, and the summary might not fit"),
            local.Decide("claude-opus-5-5", AnthropicKey, summary: true));
        Assert.Equal("ollama:llama3.2", local.ForSummary("llama3.1", NoKeys));   // the same size: it fits
    }

    [Fact]
    public void A_fast_model_this_server_does_not_know_stops_the_service()
    {
        Assert.Equal(["CERBERUS_FAST_MODEL must be a model this server knows: " + ModelRegistry.Accepted + "."],
            ServiceConfig.Validate(name => name == ModelRoles.Variable ? "haiku" : null));
        Assert.Empty(ServiceConfig.Validate(name => name == ModelRoles.Variable ? "claude-haiku-5-5" : null));
    }

    [Fact]
    public async Task Titles_and_summaries_go_to_the_model_the_roles_choose()
    {
        var conversation = new ScriptedProvider(ModelProvider.Anthropic, _ => "From the conversation's model");
        var fast = new ScriptedProvider(ModelProvider.Ollama, _ => "Login bug");
        var resolver = new ByName(new() { ["claude-opus-5-5"] = conversation, ["ollama:llama3.2"] = fast });
        var roles = new ModelRoles("ollama:llama3.2");

        var title = await new SessionTitler(resolver, roles).WriteAsync("Find the login bug.", "claude-opus-5-5", AnthropicKey, default);
        Assert.Equal("Login bug", title);
        Assert.Equal(("ollama:llama3.2", 0), (fast.Requests.Single().ModelId, conversation.Requests.Count));

        // The summary of a long Claude conversation would not fit the local model: the conversation's own writes it.
        var messages = new List<JsonObject>
        {
            JsonNode.Parse("""{"id":"u1","role":"user","parts":[{"type":"text","text":"Find the login bug."}]}""")!.AsObject(),
            JsonNode.Parse("""{"id":"a1","role":"assistant","parts":[{"type":"text","text":"It is in session.py."}]}""")!.AsObject(),
            JsonNode.Parse("""{"id":"u2","role":"user","parts":[{"type":"text","text":"Fix it."}]}""")!.AsObject(),
            JsonNode.Parse("""{"id":"a2","role":"assistant","parts":[{"type":"text","text":"Fixed."}]}""")!.AsObject(),
            JsonNode.Parse("""{"id":"u3","role":"user","parts":[{"type":"text","text":"Now add a test."}]}""")!.AsObject(),
        };
        await new ChatCompactor(resolver, roles).CompactAsync(messages, null, null, "claude-opus-5-5", AnthropicKey, default);
        Assert.Equal("claude-opus-5-5", conversation.Requests.Single().ModelId);
    }

    private sealed class ByName(Dictionary<string, IModelProvider> providers) : IModelProviderResolver
    {
        public IModelProvider Resolve(string modelId) => providers[modelId];
    }
}

[Collection("api")]
public class ControlsApiTests(ApiHost host)
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Switched_off_groups_are_gone_for_the_chat_agent_the_job_agents_and_research_and_readiness_says_which()
    {
        using var locked = host.StartVariant(DisabledTools.Variable, "shell,file-edits,notes,skills,web");
        var client = await ApiHost.AuthenticatedClientAsync(locked);

        // Readiness says what is off — healthy: it is a decision, not a fault.
        var check = (await Json(await locked.CreateClient().GetAsync("/health/ready"))).GetProperty("checks").EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "tools");
        Assert.Equal(("healthy", "switched off by CERBERUS_DISABLED_TOOLS: web, shell, file-edits, notes, skills"),
            (check.GetProperty("status").GetString(), check.GetProperty("detail").GetString()));

        // The chat agent is not offered the shell or file edits, whatever mode it is in.
        var session = (await Json(await client.PostAsJsonAsync("/sessions", new { title = "x" }))).GetProperty("id").GetString()!;
        var before = host.Tools.Requests.Count;
        var chat = await client.PostAsJsonAsync("/chat", new
        {
            id = session, mode = "BUILD", model = "llama3.1",
            messages = new[] { new { id = "u1", role = "user", parts = new[] { new { type = "text", text = "hello" } } } },
        });
        await chat.Content.ReadAsStringAsync();
        var offered = host.Tools.Requests.Skip(before).First().Tools.Select(t => t.Name).ToList();
        Assert.Contains("readFile", offered);
        Assert.DoesNotContain(offered, t => t is "bash" or "writeFile" or "editFile" or "searchSkills" or "loadSkill");

        // A job agent gets no notes and no skills.
        var job = (await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "svc", model = "llama3.1" }))).GetProperty("id").GetString()!;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while ((await client.GetFromJsonAsync<JsonElement>($"/jobs/{job}")).GetProperty("state").GetString() is not ("Succeeded" or "Failed"))
        {
            Assert.True(DateTime.UtcNow < deadline);
            await Task.Delay(30);
        }
        var agentTools = host.Tools.Requests.Last(r => r.Tools.Any(t => t.Name == "reportFinding")).Tools.Select(t => t.Name).ToList();
        Assert.Contains("readFile", agentTools);
        Assert.DoesNotContain(agentTools, t => t is "remember" or "searchSkills" or "loadSkill");

        // And research on the internet is off on this server, however it is asked for.
        var research = await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "svc", model = "llama3.1", research = true });
        Assert.Equal(HttpStatusCode.BadRequest, research.StatusCode);
        Assert.Equal("Research on the internet is switched off on this server: the \"web\" tool group is in CERBERUS_DISABLED_TOOLS.",
            (await Json(research)).GetProperty("error").GetString());
    }
}
