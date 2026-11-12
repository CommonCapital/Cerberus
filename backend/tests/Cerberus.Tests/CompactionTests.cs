using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerberus.Api.Chat;
using Cerberus.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Cerberus.Tests;

public class ChatCompactorTests
{
    private static JsonObject Message(string id, string role, string text) =>
        JsonNode.Parse(JsonSerializer.Serialize(new { id, role, parts = new[] { new { type = "text", text } } }))!.AsObject();

    private static JsonObject WithTool(string id, string tool, object input, object output) =>
        JsonNode.Parse(JsonSerializer.Serialize(new
        {
            id, role = "assistant",
            parts = new object[] { new { type = $"tool-{tool}", toolCallId = "c1", state = "output-available", input, output }, new { type = "text", text = "Done." } },
        }))!.AsObject();

    private static readonly List<JsonObject> Conversation =
    [
        Message("u1", "user", "Find the login bug."),
        WithTool("a1", "readFile", new { path = "auth.py" }, new { content = "1\tdef login(): pass" }),
        Message("u2", "user", "Fix it."),
        Message("a2", "assistant", "Fixed in auth.py."),
        Message("u3", "user", "Now add a test."),
    ];

    [Fact]
    public void The_cut_keeps_the_last_exchange_and_the_new_message_word_for_word()
    {
        Assert.Equal(2, ChatCompactor.CutIndex(Conversation));                       // from "Fix it." on is kept
        Assert.Equal(-1, ChatCompactor.CutIndex(Conversation.Take(2).ToList()));     // one user message: nothing to cut
        Assert.Equal(-1, ChatCompactor.CutIndex([Message("u1", "user", "a"), Message("u2", "user", "b")]));   // nothing before the kept part
    }

    [Fact]
    public void After_a_compaction_the_model_gets_only_what_follows_the_summarised_part()
    {
        Assert.Equal(["u2", "a2", "u3"], ChatCompactor.After(Conversation, "a1").Select(m => UiMessages.IdOf(m)!).ToArray());
        Assert.Equal(5, ChatCompactor.After(Conversation, null).Count);
        Assert.Equal(5, ChatCompactor.After(Conversation, "gone").Count);   // an unknown marker hides nothing
    }

    [Fact]
    public void The_transcript_says_who_said_what_and_what_the_tools_were_called_on()
    {
        var transcript = ChatCompactor.Transcript(Conversation.Take(2));

        Assert.Contains("User: Find the login bug.", transcript);
        Assert.Contains("Assistant called readFile {\"path\":\"auth.py\"} — returned: {\"content\":\"1\\tdef login(): pass\"}", transcript);
        Assert.Contains("Assistant: Done.", transcript);
    }

    [Fact]
    public void Compaction_is_due_when_the_conversation_nears_the_models_window()
    {
        var limits = new ChatLimits();
        Assert.False(ChatCompactor.IsDue(Conversation, null, null, "claude-haiku-4-5", limits));
        Assert.Equal(200_000, limits.WindowOf("claude-haiku-4-5"));
        Assert.Equal(8_000, limits.WindowOf("llama3.1"));     // local models are counted small

        var big = new List<JsonObject>(Conversation) { Message("a3", "assistant", new string('x', 40_000)) };   // ~10k tokens
        Assert.True(ChatCompactor.IsDue(big, null, null, "llama3.1", limits));
        Assert.False(ChatCompactor.IsDue(big, null, null, "claude-haiku-4-5", limits));
        // What is already summarised no longer counts.
        Assert.False(ChatCompactor.IsDue(big, "short summary", "a3", "llama3.1", limits));

        limits.ContextTokens = 4_000;
        Assert.True(ChatCompactor.IsDue(Conversation.Append(Message("a3", "assistant", new string('x', 4_000))).ToList(), null, null, "claude-haiku-4-5", limits));
    }

    [Fact]
    public async Task The_summary_is_written_by_the_model_from_the_earlier_part_and_any_previous_summary()
    {
        var model = new ScriptedProvider(ModelProvider.Mock, _ => "## Objective\nFix the login bug.");
        var compactor = new ChatCompactor(new SingleProviderResolver(model));

        var first = await compactor.CompactAsync(Conversation, null, null, "echo", new ModelCredentials(), default);

        Assert.Equal(new Compaction("## Objective\nFix the login bug.", "a1", 2), first);
        var sent = model.Requests[0];
        Assert.Equal(ChatCompactor.Prompt, sent.System);
        Assert.Contains("User: Find the login bug.", sent.Messages[0].Content);
        Assert.DoesNotContain("Fix it.", sent.Messages[0].Content);            // the kept part is not summarised
        Assert.DoesNotContain("Summary of the conversation before", sent.Messages[0].Content);

        // Later: more conversation, compacted again on top of the first summary.
        var longer = new List<JsonObject>(Conversation) { Message("a3", "assistant", "Test added."), Message("u4", "user", "Run it.") };
        var second = await compactor.CompactAsync(longer, first!.Summary, first.Through, "echo", new ModelCredentials(), default);

        Assert.Equal(("a2", 2), (second!.Through, second.Messages));          // "Fix it." and its reply; "Now add a test." onward is kept
        Assert.Contains("Summary of the conversation before this part:\n## Objective\nFix the login bug.", model.Requests[1].Messages[0].Content);
        Assert.Contains("User: Fix it.", model.Requests[1].Messages[0].Content);
        Assert.DoesNotContain("Find the login bug.\n", model.Requests[1].Messages[0].Content.Split("Transcript of the part to summarise:")[1]);
    }

    [Fact]
    public async Task Too_little_to_summarise_is_not_an_error_and_an_empty_summary_is()
    {
        var compactor = new ChatCompactor(new SingleProviderResolver(new ScriptedProvider(ModelProvider.Mock, _ => "   ")));

        Assert.Null(await compactor.CompactAsync(Conversation.Take(2).ToList(), null, null, "echo", new ModelCredentials(), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => compactor.CompactAsync(Conversation, null, null, "echo", new ModelCredentials(), default));
    }
}

[Collection("api")]
public class CompactionApiTests(ApiHost host)
{
    private static object UserMessage(string id, string text) => new { id, role = "user", parts = new[] { new { type = "text", text } } };

    private static async Task<string> ChatAsync(HttpClient client, string session, string id, string text)
    {
        var response = await client.PostAsJsonAsync("/chat", new { id = session, mode = "PLAN", model = "llama3.1", messages = new[] { UserMessage(id, text) } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private ChatRequest LastRequestWith(string marker)
    {
        lock (host.Tools.Requests) return host.Tools.Requests.Last(r => r.Turns.Any(t => ChatEvents.TextOf(t).Contains(marker)));
    }

    [Fact]
    public async Task A_conversation_that_outgrows_the_window_is_summarised_and_carries_on_from_the_summary()
    {
        var (client, _) = await host.NewUserAsync();
        var session = (await (await client.PostAsJsonAsync("/sessions", new { title = "chat" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var marker = $"m{Guid.NewGuid():n}";
        var limits = host.Services.GetRequiredService<ChatLimits>();

        // Two ordinary exchanges, well within the window.
        Assert.DoesNotContain("data-compaction", await ChatAsync(client, session, "u1", $"first {marker}"));
        Assert.DoesNotContain("data-compaction", await ChatAsync(client, session, "u2", $"second {marker}"));
        Assert.Equal(3, LastRequestWith($"second {marker}").Turns.Count);   // u1, a1, u2: everything in full

        limits.ContextTokens = 1_000;   // now nothing fits: the fixed part alone is over the line
        string stream;
        try
        {
            stream = await ChatAsync(client, session, "u3", $"third {marker}");
        }
        finally
        {
            limits.ContextTokens = null;
        }

        // The client is told, once, as the reply starts.
        Assert.Contains("\"type\":\"data-compaction\"", stream);
        Assert.Contains("\"data\":{\"messages\":2}", stream);

        // The model got the summary in its system prompt and, in full, only the last exchange and the new message.
        var request = LastRequestWith($"third {marker}");
        Assert.Contains("## Earlier in this conversation", request.System);
        Assert.Equal(["user", "assistant", "user"], request.Turns.Select(t => t.Role).ToArray());
        Assert.Contains($"second {marker}", ChatEvents.TextOf(request.Turns[0]));
        Assert.DoesNotContain(request.Turns, t => ChatEvents.TextOf(t).Contains($"first {marker}"));

        // Nothing is lost for the operator: every message is still in the session, with a mark of where the summary ends.
        var stored = await client.GetFromJsonAsync<JsonElement>($"/sessions/{session}");
        Assert.Equal(6, stored.GetProperty("messages").GetArrayLength());
        Assert.NotNull(stored.GetProperty("summarizedThrough").GetString());

        // The next message, back within the window, still builds on the summary and does not compact again.
        var next = await ChatAsync(client, session, "u4", $"fourth {marker}");
        Assert.DoesNotContain("data-compaction", next);
        Assert.Contains("## Earlier in this conversation", LastRequestWith($"fourth {marker}").System);
    }

    [Fact]
    public async Task The_operator_can_compact_on_demand_and_there_must_be_something_to_compact()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();
        var session = (await (await client.PostAsJsonAsync("/sessions", new { title = "chat" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        // An empty session has no model to ask; a session with one exchange has nothing to summarise.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/sessions/{session}/compact", null)).StatusCode);
        await ChatAsync(client, session, "u1", "one");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/sessions/{session}/compact", null)).StatusCode);

        await ChatAsync(client, session, "u2", "two");
        await ChatAsync(client, session, "u3", "three");
        var compacted = await client.PostAsync($"/sessions/{session}/compact", null);
        Assert.Equal(HttpStatusCode.OK, compacted.StatusCode);
        var result = await compacted.Content.ReadFromJsonAsync<JsonElement>();
        // The first exchange is summarised; the last two stay in full. Written by the model of the last reply.
        Assert.Equal((2, "llama3.1"), (result.GetProperty("messages").GetInt32(), result.GetProperty("model").GetString()));
        Assert.NotEqual("", result.GetProperty("summary").GetString());

        // Straight away again: only the exchanges that are always kept are left in full, so there is nothing more to do.
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/sessions/{session}/compact", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/sessions/{session}/compact", new { model = "gpt-0" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/sessions/{session}/compact", null)).StatusCode);
    }
}
