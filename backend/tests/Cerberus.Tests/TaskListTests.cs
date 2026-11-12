using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api.Chat;
using Cerberus.Core.Models;

namespace Cerberus.Tests;

public class TaskListTests
{
    private static JsonElement Input(params object[] tasks) => JsonDocument.Parse(JsonSerializer.Serialize(new { tasks })).RootElement.Clone();

    private static object Task(string id, string status, params string[] dependsOn) => new { id, content = $"Do {id}", status, dependsOn };

    [Fact]
    public void An_update_replaces_the_list_and_says_what_can_start_now()
    {
        var list = new TaskList(null);

        var result = JsonDocument.Parse(list.Update(Input(
            Task("read", "completed"), Task("fix", "in_progress", "read"), Task("test", "pending", "fix"), Task("docs", "pending", "read")))).RootElement;

        Assert.True(list.Changed);
        Assert.Equal(["read", "fix", "test", "docs"], list.Tasks.Select(t => t.Id).ToArray());
        Assert.Equal(["docs"], result.GetProperty("next").EnumerateArray().Select(n => n.GetString()!).ToArray());   // "test" waits for "fix"
        Assert.Equal("1 of 4 completed; can start now: docs.", result.GetProperty("summary").GetString());

        // A later update is the whole list again; what is left out is gone.
        list.Update(Input(Task("read", "completed"), Task("fix", "completed", "read")));
        Assert.Equal(2, list.Tasks.Count);
        Assert.Empty(TaskList.Runnable(list.Tasks));
    }

    [Fact]
    public void Ids_default_to_the_position_and_statuses_are_read_leniently()
    {
        var list = new TaskList(null);

        list.Update(JsonDocument.Parse("""{"tasks":[{"content":" First ","status":"In Progress"},{"content":"Second","status":"pending","dependsOn":[1]}]}""").RootElement);

        Assert.Equal(new ChatTask("1", "First", "in_progress", []), list.Tasks[0] with { DependsOn = [] });
        Assert.Equal(("2", "pending"), (list.Tasks[1].Id, list.Tasks[1].Status));
        Assert.Equal(["1"], list.Tasks[1].DependsOn);
    }

    [Fact]
    public void A_list_that_does_not_hold_together_is_refused_with_the_reason_and_the_old_one_stays()
    {
        var list = new TaskList(null);
        list.Update(Input(Task("keep", "pending")));

        string Refused(JsonElement input) => Assert.Throws<ArgumentException>(() => list.Update(input)).Message;

        Assert.Contains("the whole list", Refused(JsonDocument.Parse("{}").RootElement));
        Assert.Contains("no content", Refused(JsonDocument.Parse("""{"tasks":[{"content":"  ","status":"pending"}]}""").RootElement));
        Assert.Contains("ids must be unique", Refused(Input(Task("a", "pending"), Task("a", "pending"))));
        Assert.Contains("status must be one of", Refused(Input(Task("a", "nearly"))));
        Assert.Contains("which is not in the list", Refused(Input(Task("a", "pending", "ghost"))));
        Assert.Contains("depends on itself", Refused(Input(Task("a", "pending", "a"))));
        Assert.Contains("in a circle (a → b → c → a)", Refused(Input(Task("a", "pending", "b"), Task("b", "pending", "c"), Task("c", "pending", "a"))));
        // Started or finished before what it depends on is finished.
        Assert.Contains("is in progress but depends on \"a\", which is not completed", Refused(Input(Task("a", "pending"), Task("b", "in_progress", "a"))));
        Assert.Contains("At most 30 tasks", Refused(Input(Enumerable.Range(1, 31).Select(i => Task($"t{i}", "pending")).ToArray())));

        Assert.Equal(["keep"], list.Tasks.Select(t => t.Id).ToArray());
    }

    [Fact]
    public void The_summary_points_out_several_tasks_in_progress_and_a_finished_list()
    {
        var list = new TaskList(null);

        var two = JsonDocument.Parse(list.Update(Input(Task("a", "in_progress"), Task("b", "in_progress")))).RootElement;
        Assert.Contains("2 are in progress at once — work on one at a time", two.GetProperty("summary").GetString());

        var done = JsonDocument.Parse(list.Update(Input(Task("a", "completed"), Task("b", "cancelled")))).RootElement;
        Assert.Equal("1 of 2 completed; nothing is left to do.", done.GetProperty("summary").GetString());
        Assert.Equal("The list is empty.", JsonDocument.Parse(list.Update(Input())).RootElement.GetProperty("summary").GetString());
    }

    [Fact]
    public void The_list_is_stored_and_read_back_and_shown_to_the_model_as_a_checklist()
    {
        var list = new TaskList(null);
        list.Update(Input(Task("read", "completed"), Task("fix", "pending", "read"), Task("old", "cancelled")));

        var restored = new TaskList(list.Serialize());
        Assert.False(restored.Changed);
        Assert.Equal(list.Tasks.Select(t => (t.Id, t.Status)), restored.Tasks.Select(t => (t.Id, t.Status)));

        var section = TaskList.PromptSection(restored.Tasks);
        Assert.Contains("## Your task list", section);
        Assert.Contains("- [x] read: Do read", section);
        Assert.Contains("- [ ] fix: Do fix (after read)", section);
        Assert.Contains("- [-] old: Do old", section);
        Assert.Contains("Can start now: fix.", section);

        Assert.Equal("", TaskList.PromptSection([]));
        Assert.Empty(new TaskList("not json").Tasks);
    }
}

[Collection("api")]
public class TaskListApiTests(ApiHost host)
{
    private static object UserMessage(string id, string text) => new { id, role = "user", parts = new[] { new { type = "text", text } } };

    private static async Task<List<JsonElement>> ChatAsync(HttpClient client, string session, string id, string text, string mode = "PLAN")
    {
        var response = await client.PostAsJsonAsync("/chat", new { id = session, mode, model = "llama3.1", messages = new[] { UserMessage(id, text) } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadAsStringAsync()).Split('\n')
            .Where(l => l.StartsWith("data: {", StringComparison.Ordinal)).Select(l => JsonDocument.Parse(l[6..]).RootElement.Clone()).ToList();
    }

    [Fact]
    public async Task The_agent_keeps_a_task_list_that_the_client_sees_and_the_session_remembers()
    {
        var (client, _) = await host.NewUserAsync();
        var session = (await (await client.PostAsJsonAsync("/sessions", new { title = "chat" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var marker = $"m{Guid.NewGuid():n}";

        // A new session has no list.
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>($"/sessions/{session}")).GetProperty("tasks").GetArrayLength());

        var chunks = await ChatAsync(client, session, "u1", $"tasks: read the code; fix {marker}; add a test");

        // The tool runs on the server, within the reply; the client is handed its result.
        var input = chunks.Single(c => c.GetProperty("type").GetString() == "tool-input-available");
        Assert.Equal(("updateTasks", true), (input.GetProperty("toolName").GetString(), input.GetProperty("providerExecuted").GetBoolean()));
        var output = chunks.Single(c => c.GetProperty("type").GetString() == "tool-output-available").GetProperty("output");
        Assert.Equal(["in_progress", "pending", "pending"], output.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("status").GetString()!).ToArray());
        Assert.Equal("0 of 3 completed.", output.GetProperty("summary").GetString());   // 2 and 3 wait for the one in progress

        // Stored with the session.
        var stored = await client.GetFromJsonAsync<JsonElement>($"/sessions/{session}");
        Assert.Equal(["read the code", $"fix {marker}", "add a test"], stored.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("content").GetString()!).ToArray());

        // And the next request reminds the model of it.
        await ChatAsync(client, session, "u2", $"carry on {marker}");
        string system;
        lock (host.Tools.Requests) system = host.Tools.Requests.Last(r => r.Turns.Any(t => ChatEvents.TextOf(t).Contains($"carry on {marker}"))).System!;
        Assert.Contains("## Your task list", system);
        Assert.Contains("- [>] 1: read the code", system);
        Assert.Contains($"- [ ] 2: fix {marker} (after 1)", system);
    }

    [Fact]
    public async Task The_task_tool_is_offered_in_both_modes_and_a_bad_list_goes_back_to_the_model_as_an_error()
    {
        var (client, _) = await host.NewUserAsync();
        var session = (await (await client.PostAsJsonAsync("/sessions", new { title = "chat" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var marker = $"m{Guid.NewGuid():n}";

        foreach (var mode in new[] { "PLAN", "BUILD" })
        {
            await ChatAsync(client, session, $"u-{mode}", $"hello {marker} {mode}", mode);
            ChatRequest request;
            lock (host.Tools.Requests) request = host.Tools.Requests.Last(r => r.Turns.Any(t => ChatEvents.TextOf(t).Contains($"hello {marker} {mode}")));
            Assert.Contains(request.Tools, t => t.Name == "updateTasks");
            Assert.Contains("## Task list", request.System);   // told when to use it
        }

        // More than 30 tasks: refused, reported to the model as a failed call, and nothing is stored.
        var tooMany = string.Join("; ", Enumerable.Range(1, 31).Select(i => $"step {i}"));
        var chunks = await ChatAsync(client, session, "u3", $"tasks: {tooMany}");
        var error = chunks.Single(c => c.GetProperty("type").GetString() == "tool-output-error");
        Assert.Contains("At most 30 tasks", error.GetProperty("errorText").GetString());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>($"/sessions/{session}")).GetProperty("tasks").GetArrayLength());
    }
}
