using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerberus.Api.Chat;
using Cerberus.Core.Models;
using Cerberus.Models;

namespace Cerberus.Tests;

/// <summary>A test double that follows the dev model's rules under another provider's name, and records its requests.</summary>
public sealed class RuleFollowingProvider(ModelProvider provider) : IModelProvider
{
    private readonly MockProvider _rules = new();

    public ModelProvider Provider => provider;

    public List<ChatRequest> Requests { get; } = [];

    public IAsyncEnumerable<string> StreamAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default) =>
        _rules.StreamAsync(request, cancellationToken);

    /// <summary>Set by a test to hold the model back until the test lets it answer.</summary>
    public Func<ChatRequest, Task>? BeforeReply { get; set; }

    /// <summary>How many of the next calls fail as an overloaded provider would.</summary>
    public int FailNext { get; set; }

    public async IAsyncEnumerable<ChatEvent> StreamChatAsync(
        ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        lock (Requests) Requests.Add(request with { Turns = [.. request.Turns] });
        // Fails the next calls as an overloaded provider would, before anything is produced.
        if (FailNext > 0)
        {
            FailNext--;
            throw new HttpRequestException("answered 529", null, (System.Net.HttpStatusCode)529);
        }
        if (BeforeReply is { } hold) await hold(request);
        await foreach (var e in _rules.StreamChatAsync(request, cancellationToken)) yield return e;
    }
}

public class UiMessagesTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonObject O(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Incoming_messages_replace_stored_ones_with_the_same_id_and_append_new_ones()
    {
        var stored = new[] { J("""{"id":"u1","role":"user","parts":[{"type":"text","text":"hi"}]}"""), J("""{"id":"a1","role":"assistant","parts":[]}""") };
        var incoming = new[] { J("""{"id":"a1","role":"assistant","parts":[{"type":"text","text":"updated"}]}"""), J("""{"id":"u2","role":"user","parts":[]}""") };

        var merged = UiMessages.Merge(stored, incoming);

        Assert.Equal(["u1", "a1", "u2"], merged.Select(m => UiMessages.IdOf(m)!).ToArray());
        Assert.Equal("updated", merged[1]["parts"]![0]!["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"id":"m","role":"user","parts":[]}""", true)]
    [InlineData("""{"id":"m","role":"assistant","parts":[{"type":"text","text":"x"}]}""", true)]
    [InlineData("""{"role":"user","parts":[]}""", false)]
    [InlineData("""{"id":"m","role":"user"}""", false)]
    [InlineData("""{"id":"m","role":"robot","parts":[]}""", false)]
    [InlineData("""{"id":1,"role":"user","parts":[]}""", false)]
    [InlineData("\"just text\"", false)]
    public void A_message_needs_an_id_a_known_role_and_parts(string json, bool valid)
    {
        Assert.Equal(valid, UiMessages.IsValid(J(json)));
    }

    [Fact]
    public void An_assistant_message_becomes_one_turn_per_step_each_followed_by_its_tool_results()
    {
        var turns = UiMessages.ToTurns(
        [
            O("""{"id":"u","role":"user","parts":[{"type":"text","text":"read a and b"}]}"""),
            O("""
              {"id":"a","role":"assistant","parts":[
                {"type":"step-start"},
                {"type":"text","text":"Reading."},
                {"type":"tool-readFile","toolCallId":"c1","state":"output-available","input":{"path":"a"},"output":{"content":"AAA"}},
                {"type":"tool-readFile","toolCallId":"c2","state":"output-error","input":{"path":"b"},"errorText":"not found"},
                {"type":"step-start"},
                {"type":"text","text":"Done."}
              ]}
              """),
        ]);

        Assert.Equal(["user", "assistant", "tool", "assistant"], turns.Select(t => t.Role).ToArray());

        var step = turns[1].Parts;
        Assert.Equal("Reading.", Assert.IsType<TextPart>(step[0]).Text);
        var call = Assert.IsType<ToolCallPart>(step[1]);
        Assert.Equal(("c1", "readFile", "a"), (call.Id, call.Name, call.Input.GetProperty("path").GetString()));

        var results = turns[2].Parts.Cast<ToolResultPart>().ToList();
        Assert.Equal(new ToolResultPart("c1", "readFile", """{"content":"AAA"}"""), results[0]);
        Assert.Equal(new ToolResultPart("c2", "readFile", "not found", IsError: true), results[1]);

        Assert.Equal("Done.", ChatEvents.TextOf(turns[3]));
    }

    [Fact]
    public void A_tool_call_without_a_result_is_answered_with_an_error_rather_than_left_dangling()
    {
        var turns = UiMessages.ToTurns(
        [
            O("""{"id":"a","role":"assistant","parts":[{"type":"tool-bash","toolCallId":"c1","state":"input-available","input":{"command":"ls"}}]}"""),
        ]);

        var result = Assert.IsType<ToolResultPart>(Assert.Single(turns[1].Parts));
        Assert.True(result.IsError);
        Assert.Equal("c1", result.ToolCallId);
    }

    [Fact]
    public void String_outputs_are_passed_as_is_and_structured_ones_as_json()
    {
        var turns = UiMessages.ToTurns(
        [
            O("""
              {"id":"a","role":"assistant","parts":[
                {"type":"tool-bash","toolCallId":"c1","state":"output-available","input":{},"output":"plain text"},
                {"type":"tool-glob","toolCallId":"c2","state":"output-available","input":{},"output":{"files":["a","b"]}}
              ]}
              """),
        ]);

        var results = turns[1].Parts.Cast<ToolResultPart>().Select(r => r.Content).ToArray();
        Assert.Equal(["plain text", """{"files":["a","b"]}"""], results);
    }

    [Fact]
    public void Only_reasoning_the_provider_signed_is_sent_back()
    {
        var turns = UiMessages.ToTurns(
        [
            O("""
              {"id":"a","role":"assistant","parts":[
                {"type":"reasoning","text":"display only"},
                {"type":"reasoning","text":"signed","providerMetadata":{"anthropic":{"signature":"sig-1"}}},
                {"type":"text","text":"answer"}
              ]}
              """),
        ]);

        var parts = turns.Single().Parts;
        Assert.Equal(2, parts.Count);
        Assert.Equal(new ReasoningPart("signed", "sig-1"), parts[0]);
    }

    [Fact]
    public void Pending_tool_calls_are_detected()
    {
        Assert.True(UiMessages.HasPendingToolCalls(O("""{"id":"a","role":"assistant","parts":[{"type":"tool-bash","toolCallId":"c","state":"input-available"}]}""")));
        Assert.False(UiMessages.HasPendingToolCalls(O("""{"id":"a","role":"assistant","parts":[{"type":"tool-bash","toolCallId":"c","state":"output-available"},{"type":"text","text":"x"}]}""")));
        Assert.False(UiMessages.HasPendingToolCalls(O("""{"id":"a","role":"assistant","parts":[{"type":"tool-bash","toolCallId":"c","state":"output-error"}]}""")));
    }
}

public class ChatToolsTests
{
    [Fact]
    public void Plan_mode_offers_only_read_only_file_tools_plus_the_server_tools()
    {
        // The server tools — the skills library and the agent's own task list — change nothing on the user's machine.
        Assert.Equal(["readFile", "listDirectory", "glob", "grep", "searchSkills", "loadSkill", "updateTasks"],
            ChatTools.For("PLAN").Select(t => t.Name).ToArray());
        Assert.Equal(["readFile", "listDirectory", "glob", "grep", "writeFile", "editFile", "bash", "searchSkills", "loadSkill", "updateTasks"],
            ChatTools.For("BUILD").Select(t => t.Name).ToArray());
    }

    [Fact]
    public void Every_tool_has_an_object_schema_and_a_description()
    {
        foreach (var tool in ChatTools.For("BUILD"))
        {
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        }
    }

    [Theory]
    [InlineData("BUILD", true)]
    [InlineData("PLAN", true)]
    [InlineData("build", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Modes_are_build_or_plan(string? mode, bool valid)
    {
        Assert.Equal(valid, ChatTools.IsValidMode(mode));
    }

    [Fact]
    public void Server_tools_search_and_load_at_the_passive_tier()
    {
        using var fake = new FakeSkills();
        var library = fake.Library();
        JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();

        var found = JsonDocument.Parse(ChatTools.ExecuteServerTool("searchSkills", Input("""{"query":"tls"}"""), library)).RootElement;
        Assert.Equal("reviewing-tls-configuration", found.GetProperty("results")[0].GetProperty("name").GetString());

        var loaded = JsonDocument.Parse(ChatTools.ExecuteServerTool("loadSkill", Input("""{"name":"reviewing-tls-configuration"}"""), library)).RootElement;
        Assert.Contains("Step two", loaded.GetProperty("content").GetString());

        // The offensive skill exists, but the chat agent can neither find nor load it.
        var none = JsonDocument.Parse(ChatTools.ExecuteServerTool("searchSkills", Input("""{"query":"exercise"}"""), library)).RootElement;
        Assert.Equal(0, none.GetProperty("results").GetArrayLength());
        Assert.Throws<UnauthorizedAccessException>(() =>
            ChatTools.ExecuteServerTool("loadSkill", Input("""{"name":"running-an-authorized-intrusion-exercise"}"""), library));

        Assert.Throws<KeyNotFoundException>(() => ChatTools.ExecuteServerTool("loadSkill", Input("""{"name":"nope"}"""), library));
        Assert.Throws<ArgumentException>(() => ChatTools.ExecuteServerTool("searchSkills", Input("{}"), library));
    }
}

public class OpenAiCompatibleProviderTests
{
    private sealed class CannedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        }
    }

    private static readonly JsonElement Schema = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""").RootElement.Clone();

    private static ChatRequest Request(params ChatTurn[] turns) =>
        new("llama3.1", "be brief", turns, [new ToolDefinition("readFile", "Read a file.", Schema)], new ModelCredentials());

    private static async Task<(List<ChatEvent> Events, JsonElement Sent)> Run(string sse, params ChatTurn[] turns)
    {
        var handler = new CannedHandler(HttpStatusCode.OK, sse);
        var events = new List<ChatEvent>();
        await foreach (var e in new OllamaProvider(new HttpClient(handler)).StreamChatAsync(Request(turns))) events.Add(e);
        return (events, JsonDocument.Parse(handler.RequestBody!).RootElement.Clone());
    }

    [Fact]
    public async Task Text_tool_calls_usage_and_the_finish_reason_are_read_from_the_stream()
    {
        const string sse = """
            data: {"choices":[{"delta":{"role":"assistant","content":"Let me "}}]}

            data: {"choices":[{"delta":{"content":"look."}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"readFile","arguments":"{\"pa"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_b","type":"function","function":{"name":"readFile","arguments":""}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"a.txt\"}"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"path\":\"b.txt\"}"}}]}}]}

            data: not json at all

            data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}

            data: {"choices":[],"usage":{"prompt_tokens":42,"completion_tokens":7}}

            data: [DONE]

            """;

        var (events, _) = await Run(sse, ChatTurn.User("read both"));

        Assert.Equal("Let me look.", string.Concat(events.OfType<TextDelta>().Select(t => t.Text)));
        Assert.Equal([new ToolCallStart("call_a", "readFile"), new ToolCallStart("call_b", "readFile")], events.OfType<ToolCallStart>().ToArray());
        Assert.Equal(
        [
            new ToolCallComplete("call_a", "readFile", """{"path":"a.txt"}"""),
            new ToolCallComplete("call_b", "readFile", """{"path":"b.txt"}"""),
        ], events.OfType<ToolCallComplete>().ToArray());
        Assert.Equal(new UsageReport(42, 7), events.OfType<UsageReport>().Single());
        Assert.Equal(new StepFinished("tool_calls"), events[^1]);
    }

    [Fact]
    public async Task Reasoning_fields_some_servers_send_are_surfaced_as_reasoning()
    {
        const string sse = """
            data: {"choices":[{"delta":{"reasoning_content":"thinking "}}]}

            data: {"choices":[{"delta":{"reasoning":"hard"}}]}

            data: {"choices":[{"delta":{"content":"answer"},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        var (events, _) = await Run(sse, ChatTurn.User("why"));

        Assert.Equal(["ReasoningDelta", "ReasoningDelta", "ReasoningEnd", "TextDelta", "StepFinished"], events.Select(e => e.GetType().Name).ToArray());
        Assert.Equal("thinking hard", string.Concat(events.OfType<ReasoningDelta>().Select(r => r.Text)));
    }

    [Fact]
    public async Task The_request_carries_the_system_prompt_tools_and_the_tool_exchange()
    {
        var input = JsonDocument.Parse("""{"path":"a.txt"}""").RootElement.Clone();
        var (_, sent) = await Run("data: [DONE]\n\n",
            ChatTurn.User("read a.txt"),
            new ChatTurn("assistant", [new TextPart("Reading."), new ToolCallPart("call_a", "readFile", input)]),
            new ChatTurn("tool", [new ToolResultPart("call_a", "readFile", "AAA"), new ToolResultPart("call_x", "readFile", "missing", IsError: true)]));

        Assert.True(sent.GetProperty("stream").GetBoolean());
        Assert.Equal("readFile", sent.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("object", sent.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());

        var messages = sent.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user", "assistant", "tool", "tool"], messages.Select(m => m.GetProperty("role").GetString()!).ToArray());
        var call = messages[2].GetProperty("tool_calls")[0];
        Assert.Equal("call_a", call.GetProperty("id").GetString());
        Assert.Equal("""{"path":"a.txt"}""", call.GetProperty("function").GetProperty("arguments").GetString());   // arguments are a JSON string
        Assert.Equal(("call_a", "AAA"), (messages[3].GetProperty("tool_call_id").GetString(), messages[3].GetProperty("content").GetString()));
        Assert.Equal("Error: missing", messages[4].GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_provider_error_surfaces_with_the_providers_explanation()
    {
        var provider = new OllamaProvider(new HttpClient(new CannedHandler(HttpStatusCode.NotFound, """{"error":"model 'llama3.1' not found"}""")));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in provider.StreamChatAsync(Request(ChatTurn.User("hi")))) { }
        });

        Assert.Contains("404", ex.Message);
        Assert.Contains("not found", ex.Message);
    }
}

[Collection("api")]
public class ChatApiTests(ApiHost host)
{
    // An Ollama model id; in the test host it is served by RuleFollowingProvider, not by a model.
    private const string Model = "llama3.1";

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static object UserMessage(string id, string text) =>
        new { id, role = "user", parts = new[] { new { type = "text", text } } };

    private static async Task<string> NewSessionAsync(HttpClient client) =>
        (await Json(await client.PostAsJsonAsync("/sessions", new { title = "chat" }))).GetProperty("id").GetString()!;

    /// <summary>Posts to /chat and returns the stream's chunks, in order.</summary>
    private static async Task<List<JsonElement>> ChatAsync(HttpClient client, string sessionId, object[] messages, string mode = "BUILD", string model = Model)
    {
        var response = await client.PostAsJsonAsync("/chat", new { id = sessionId, messages, mode, model });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("v1", response.Headers.GetValues("x-vercel-ai-ui-message-stream").Single());

        var lines = (await response.Content.ReadAsStringAsync()).Split('\n').Where(l => l.StartsWith("data: ")).Select(l => l[6..]).ToList();
        Assert.Equal("[DONE]", lines[^1]);
        return lines.Take(lines.Count - 1).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    private static string[] Types(IEnumerable<JsonElement> chunks) => chunks.Select(c => c.GetProperty("type").GetString()!).ToArray();

    private static async Task<List<JsonElement>> StoredAsync(HttpClient client, string sessionId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/sessions/{sessionId}")).GetProperty("messages").EnumerateArray().ToList();

    [Fact]
    public async Task Requests_are_validated_like_the_typescript_server()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client);
        var message = UserMessage("u1", "hi");

        async Task<HttpStatusCode> Post(object body) => (await client.PostAsJsonAsync("/chat", body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { id = session, messages = Array.Empty<object>(), mode = "BUILD", model = Model }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { id = session, mode = "BUILD", model = Model }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { id = session, messages = new[] { new { role = "user" } }, mode = "BUILD", model = Model }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { id = session, messages = new[] { message }, mode = "DESTROY", model = Model }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { id = session, messages = new[] { message }, mode = "BUILD", model = "no-such-model" }));
        Assert.Equal(HttpStatusCode.NotFound, await Post(new { id = "missing", messages = new[] { message }, mode = "BUILD", model = Model }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().PostAsJsonAsync("/chat", new { })).StatusCode);

        Assert.Empty(await StoredAsync(client, session));   // nothing invalid was stored
    }

    [Fact]
    public async Task A_plain_reply_is_streamed_and_stored_with_its_metadata()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client);

        var chunks = await ChatAsync(client, session, [UserMessage("u1", "hello")]);

        var types = Types(chunks);
        Assert.Equal(["start", "start-step", "text-start"], types.Take(3).ToArray());
        Assert.Equal(["text-end", "finish-step", "finish"], types.TakeLast(3).ToArray());
        Assert.Equal("Echo: hello ", string.Concat(chunks.Where(c => c.GetProperty("type").GetString() == "text-delta").Select(c => c.GetProperty("delta").GetString())));
        Assert.Equal(10, chunks[^1].GetProperty("messageMetadata").GetProperty("usage").GetProperty("totalTokens").GetInt32());

        var stored = await StoredAsync(client, session);
        Assert.Equal(["user", "assistant"], stored.Select(m => m.GetProperty("role").GetString()!).ToArray());
        Assert.Equal("BUILD", stored[0].GetProperty("metadata").GetProperty("mode").GetString());   // incoming message stamped
        Assert.Equal(chunks[0].GetProperty("messageId").GetString(), stored[1].GetProperty("id").GetString());
        Assert.Equal(["step-start", "text"], stored[1].GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("type").GetString()!).ToArray());
        Assert.Equal(Model, stored[1].GetProperty("metadata").GetProperty("model").GetString());
    }

    [Fact]
    public async Task A_client_tool_call_ends_the_reply_unsaved_and_the_resubmitted_message_is_continued()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client);
        var user = UserMessage("u1", "read notes.txt");

        // 1. The model asks for the file. The reply ends there, waiting for the client.
        var first = await ChatAsync(client, session, [user]);
        Assert.Equal(
            ["start", "start-step", "text-start", "text-delta", "text-delta", "text-end", "tool-input-start", "tool-input-delta", "tool-input-delta", "tool-input-available", "finish-step", "finish"],
            Types(first));
        var available = first.Single(c => c.GetProperty("type").GetString() == "tool-input-available");
        Assert.Equal("readFile", available.GetProperty("toolName").GetString());
        Assert.Equal("notes.txt", available.GetProperty("input").GetProperty("path").GetString());
        Assert.False(available.TryGetProperty("providerExecuted", out _));   // the client must run it
        Assert.Empty(await StoredAsync(client, session));                    // nothing stored while the call is pending

        // 2. The client ran the tool and sends the user message plus the assistant message with the result.
        var assistantId = first[0].GetProperty("messageId").GetString()!;
        var callId = available.GetProperty("toolCallId").GetString()!;
        object assistant = new
        {
            id = assistantId,
            role = "assistant",
            parts = new object[]
            {
                new { type = "step-start" },
                new { type = "text", text = "Calling readFile. ", state = "done" },
                new { type = "tool-readFile", toolCallId = callId, state = "output-available", input = new { path = "notes.txt" }, output = new { content = "remember the milk" } },
            },
        };
        var second = await ChatAsync(client, session, [user, assistant]);

        Assert.Equal(assistantId, second[0].GetProperty("messageId").GetString());   // same message, continued
        var reply = string.Concat(second.Where(c => c.GetProperty("type").GetString() == "text-delta").Select(c => c.GetProperty("delta").GetString()));
        Assert.Contains("readFile returned:", reply);
        Assert.Contains("remember the milk", reply);

        // 3. Now it is whole, and stored once: one user message, one assistant message with both steps.
        var stored = await StoredAsync(client, session);
        Assert.Equal(["u1", assistantId], stored.Select(m => m.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(["step-start", "text", "tool-readFile", "step-start", "text"],
            stored[1].GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("type").GetString()!).ToArray());

        // The model was shown its own call and the client's result.
        var lastRequest = host.Tools.Requests.Last(r => r.Turns.Any(t => t.Role == "tool" && t.Parts.OfType<ToolResultPart>().Any(p => p.ToolCallId == callId)));
        Assert.Equal(["user", "assistant", "tool"], lastRequest.Turns.Select(t => t.Role).ToArray());
    }

    [Fact]
    public async Task A_server_tool_runs_in_the_backend_within_the_same_reply()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client);

        var chunks = await ChatAsync(client, session, [UserMessage("u1", "skills about tls")]);

        var types = Types(chunks);
        Assert.Equal(2, types.Count(t => t == "start-step"));   // the model was called again with the result
        var input = chunks.Single(c => c.GetProperty("type").GetString() == "tool-input-available");
        Assert.Equal("searchSkills", input.GetProperty("toolName").GetString());
        Assert.True(input.GetProperty("providerExecuted").GetBoolean());
        var output = chunks.Single(c => c.GetProperty("type").GetString() == "tool-output-available");
        Assert.True(output.GetProperty("providerExecuted").GetBoolean());
        Assert.InRange(output.GetProperty("output").GetProperty("results").GetArrayLength(), 1, 3);
        Assert.True(Array.IndexOf(types, "tool-output-available") < Array.LastIndexOf(types, "start-step"));

        var parts = (await StoredAsync(client, session))[1].GetProperty("parts").EnumerateArray().ToList();
        var tool = parts.Single(p => p.GetProperty("type").GetString() == "tool-searchSkills");
        Assert.Equal("output-available", tool.GetProperty("state").GetString());
        Assert.True(tool.GetProperty("providerExecuted").GetBoolean());
    }

    [Fact]
    public async Task Plan_mode_gives_the_model_no_tools_that_change_anything()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client);
        var before = host.Tools.Requests.Count;

        await ChatAsync(client, session, [UserMessage("u1", "plan please")], mode: "plan");   // case-insensitive
        var request = host.Tools.Requests.Skip(before).Last();

        var names = request.Tools.Select(t => t.Name).ToArray();
        Assert.DoesNotContain("writeFile", names);
        Assert.DoesNotContain("editFile", names);
        Assert.DoesNotContain("bash", names);
        Assert.Contains("readFile", names);
        Assert.Contains("## Mode: PLAN", request.System);
        Assert.Equal("PLAN", (await StoredAsync(client, session))[1].GetProperty("metadata").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task Sending_a_message_again_replaces_it_instead_of_duplicating_it()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client);

        await ChatAsync(client, session, [UserMessage("u1", "first")]);
        await ChatAsync(client, session, [UserMessage("u1", "first, edited")]);

        var stored = await StoredAsync(client, session);
        Assert.Equal(1, stored.Count(m => m.GetProperty("id").GetString() == "u1"));
        Assert.Equal("first, edited", stored.Single(m => m.GetProperty("id").GetString() == "u1").GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_failing_model_is_reported_in_the_stream_and_nothing_is_stored()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client);

        // In the test host the OpenAI ids are served by a stand-in that always fails.
        var chunks = await ChatAsync(client, session, [UserMessage("u1", "hi")], model: "gpt-5.4-mini");

        Assert.Equal("error", Types(chunks)[^1]);
        Assert.Equal("not used", chunks[^1].GetProperty("errorText").GetString());
        Assert.Empty(await StoredAsync(client, session));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("a@")]
    [InlineData("a@b")]
    [InlineData("a b@c.de")]
    [InlineData("@c.de")]
    public async Task Registration_needs_a_real_looking_email(string email)
    {
        var response = await host.CreateClient().PostAsJsonAsync("/auth/register", new { email, password = "secret123" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("A valid email and a password (min 6 characters) are required", (await Json(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Auth_answers_like_the_typescript_server()
    {
        var client = host.CreateClient();
        var email = $"{Guid.NewGuid():n}@test.dev";

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/register", new { email, password = "short" })).StatusCode);
        var registered = await client.PostAsJsonAsync("/auth/register", new { email, password = "secret123" });
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var token = (await Json(registered)).GetProperty("token").GetString();

        var duplicate = await client.PostAsJsonAsync("/auth/register", new { email, password = "secret123" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("A user with this email already exists", (await Json(duplicate)).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/auth/login", new { email, password = "wrong-password" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/auth/login", new { email, password = "secret123" })).StatusCode);

        // Logging out revokes that token, and is harmless without one.
        var request = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        Assert.True((await Json(await client.SendAsync(request))).GetProperty("success").GetBoolean());
        var after = new HttpRequestMessage(HttpMethod.Get, "/sessions");
        after.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var rejected = await client.SendAsync(after);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal("Unauthorized. Run /login to continue.", (await Json(rejected)).GetProperty("error").GetString());
        Assert.True((await Json(await client.PostAsync("/auth/logout", null))).GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task Sessions_answer_like_the_typescript_server()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();

        var created = await client.PostAsJsonAsync("/sessions", new { title = "First" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var session = await Json(created);
        foreach (var field in new[] { "id", "userId", "title", "createdAt", "updatedAt", "messages" })
        {
            Assert.True(session.TryGetProperty(field, out _), $"missing {field}");
        }
        Assert.Equal(0, session.GetProperty("messages").GetArrayLength());
        var id = session.GetProperty("id").GetString();

        var missingTitle = await client.PostAsJsonAsync("/sessions", new { });
        Assert.Equal(HttpStatusCode.BadRequest, missingTitle.StatusCode);
        Assert.Equal("Invalid request body", (await Json(missingTitle)).GetProperty("error").GetString());

        var list = await client.GetFromJsonAsync<JsonElement>("/sessions");
        var listed = list.EnumerateArray().Single();
        Assert.Equal(["id", "title", "createdAt"], listed.EnumerateObject().Select(p => p.Name).ToArray());

        Assert.Equal("First", (await client.GetFromJsonAsync<JsonElement>($"/sessions/{id}")).GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/sessions/{id}")).StatusCode);
        Assert.Equal("Session not found", (await Json(await client.GetAsync("/sessions/missing"))).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_body_that_is_not_json_gets_a_json_error()
    {
        var (client, _) = await host.NewUserAsync();

        var response = await client.PostAsync("/sessions", new StringContent("{not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid request body", (await Json(response)).GetProperty("error").GetString());
    }
}
