using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api;
using Cerberus.Api.Chat;
using Cerberus.Core.Models;

namespace Cerberus.Tests;

public class DelegationToolTests
{
    [Fact]
    public void The_task_tool_is_offered_only_where_delegation_is_possible()
    {
        Assert.DoesNotContain("task", ChatTools.For("PLAN").Select(t => t.Name));
        Assert.DoesNotContain("task", ChatTools.For("BUILD").Select(t => t.Name));
        Assert.Equal("task", ChatTools.For("PLAN", delegation: true)[^1].Name);
        Assert.Equal("task", ChatTools.For("BUILD", delegation: true)[^1].Name);
        // The client runs it: the helper's file tools are on the operator's machine.
        Assert.False(ChatTools.IsServerTool("task"));
    }

    [Fact]
    public void The_task_tool_says_what_a_helper_can_and_cannot_do()
    {
        var tool = ChatTools.For("BUILD", delegation: true)[^1];

        Assert.Contains("own context", tool.Description);
        Assert.Contains("sees nothing of this conversation", tool.Description);
        Assert.Contains("cannot change files", tool.Description);
        Assert.Equal(["description", "prompt"], tool.InputSchema.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).ToArray());
    }
}

[Collection("api")]
public class SubagentApiTests(ApiHost host)
{
    private const string Model = "llama3.1";

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static object UserMessage(string id, string text) => new { id, role = "user", parts = new[] { new { type = "text", text } } };

    private static async Task<string> NewSessionAsync(HttpClient client, string title, string? parentId = null)
    {
        var response = await client.PostAsJsonAsync("/sessions", new { title, parentId });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await Json(response)).GetProperty("id").GetString()!;
    }

    private static async Task<List<JsonElement>> ChatAsync(HttpClient client, string session, object[] messages, string mode, bool? subagents = null)
    {
        var response = await client.PostAsJsonAsync("/chat", new { id = session, mode, model = Model, messages, subagents });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line["data: ".Length..])
            .Where(data => data != "[DONE]")
            .Select(data => JsonDocument.Parse(data).RootElement.Clone())
            .ToList();
    }

    private static string TextOf(IEnumerable<JsonElement> chunks) =>
        string.Concat(chunks.Where(c => c.GetProperty("type").GetString() == "text-delta").Select(c => c.GetProperty("delta").GetString()));

    private ChatRequest RequestWith(string marker)
    {
        lock (host.Tools.Requests) return host.Tools.Requests.Last(r => r.Turns.Any(t => ChatEvents.TextOf(t).Contains(marker)));
    }

    [Fact]
    public async Task A_helpers_conversation_hangs_under_a_chat_of_the_same_operator_and_is_not_listed()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();
        var parent = await NewSessionAsync(client, "main chat");

        var child = await NewSessionAsync(client, "find the loader", parent);

        Assert.Equal(parent, (await Json(await client.GetAsync($"/sessions/{child}"))).GetProperty("parentId").GetString());
        Assert.Equal(JsonValueKind.Null, (await Json(await client.GetAsync($"/sessions/{parent}"))).GetProperty("parentId").ValueKind);
        var listed = (await Json(await client.GetAsync("/sessions"))).EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Equal([parent], listed);

        // Helpers have no helpers; and nobody hangs a conversation under someone else's chat.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/sessions", new { title = "deeper", parentId = child })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync("/sessions", new { title = "mine", parentId = parent })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/sessions", new { title = "x", parentId = "nope" })).StatusCode);
    }

    [Fact]
    public async Task The_agent_can_delegate_only_when_the_client_says_it_can_run_helpers()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client, "main chat");
        var marker = $"m{Guid.NewGuid():n}";

        // Without the client's word, there is no task tool: the dev model just echoes.
        var plain = await ChatAsync(client, session, [UserMessage("u1", $"delegate: read {marker}.py and report")], "BUILD");
        Assert.DoesNotContain(plain, c => c.GetProperty("type").GetString() == "tool-input-available");
        Assert.DoesNotContain("task", RequestWith(marker).Tools.Select(t => t.Name));
        Assert.DoesNotContain("## Helpers", RequestWith(marker).System);

        var second = $"n{Guid.NewGuid():n}";
        var delegated = await ChatAsync(client, session, [UserMessage("u2", $"delegate: read {second}.py and report")], "BUILD", subagents: true);

        var call = delegated.Single(c => c.GetProperty("type").GetString() == "tool-input-available");
        Assert.Equal("task", call.GetProperty("toolName").GetString());
        Assert.False(call.TryGetProperty("providerExecuted", out _));   // the client runs the helper
        Assert.Equal($"read {second}.py and report", call.GetProperty("input").GetProperty("prompt").GetString());
        Assert.Equal($"read {second}.py and report", call.GetProperty("input").GetProperty("description").GetString());
        var request = RequestWith(second);
        Assert.Equal("task", request.Tools[^1].Name);
        Assert.Contains("## Helpers", request.System);
        Assert.DoesNotContain("## You are a helper", request.System);

        // The client ran the helper and answers the call with its report; the agent carries on from it.
        var assistantId = delegated[0].GetProperty("messageId").GetString()!;
        object assistant = new
        {
            id = assistantId,
            role = "assistant",
            parts = new object[]
            {
                new { type = "step-start" },
                new
                {
                    type = "tool-task", toolCallId = call.GetProperty("toolCallId").GetString(), state = "output-available",
                    input = call.GetProperty("input"), output = new { report = "loader.py line 7 calls pickle.loads", toolCalls = 2, complete = true },
                },
            },
        };
        var continued = await ChatAsync(client, session, [UserMessage("u2", $"delegate: read {second}.py and report"), assistant], "BUILD", subagents: true);
        Assert.Contains("task returned:", TextOf(continued));
        Assert.Contains("loader.py line 7 calls pickle.loads", TextOf(continued));
    }

    [Fact]
    public async Task A_helper_only_looks_reports_to_the_agent_and_cannot_delegate_whatever_the_request_says()
    {
        var (client, _) = await host.NewUserAsync();
        var parent = await NewSessionAsync(client, "main chat");
        var child = await NewSessionAsync(client, "find the loader", parent);
        var marker = $"h{Guid.NewGuid():n}";

        // The request asks for everything a helper must not have: write tools and helpers of its own.
        var reply = await ChatAsync(client, child, [UserMessage("u1", $"delegate: look at {marker}")], "BUILD", subagents: true);

        Assert.DoesNotContain(reply, c => c.GetProperty("type").GetString() == "tool-input-available");
        var request = RequestWith(marker);
        var offered = request.Tools.Select(t => t.Name).ToList();
        Assert.Equal(ChatTools.For("PLAN").Select(t => t.Name).ToList(), offered);
        Assert.DoesNotContain("task", offered);
        Assert.DoesNotContain("writeFile", offered);
        Assert.DoesNotContain("bash", offered);
        Assert.Contains("## You are a helper", request.System);
        Assert.Contains("the only thing the other agent will see", request.System);
        Assert.Contains("## Mode: PLAN", request.System);
        Assert.DoesNotContain("## Helpers", request.System);

        // What it said is kept with its own conversation, stamped as what it was: read-only.
        var stored = (await Json(await client.GetAsync($"/sessions/{child}"))).GetProperty("messages");
        Assert.Equal(2, stored.GetArrayLength());
        Assert.Equal("PLAN", stored[0].GetProperty("metadata").GetProperty("mode").GetString());
    }
}
