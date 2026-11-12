using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerberus.Api.Chat;
using Cerberus.Data;

namespace Cerberus.Tests;

public class SessionExportTests
{
    private static readonly DateTime When = new(2026, 11, 12, 15, 40, 0, DateTimeKind.Utc);

    private static JsonObject O(string json) => JsonNode.Parse(json)!.AsObject();

    private static string Export(string title, params string[] messages) =>
        SessionExport.Markdown(
            new Session { Id = "0123456789abcdef", Title = title, CreatedAt = new DateTime(2026, 11, 12, 9, 5, 0, DateTimeKind.Utc) },
            messages.Select(O).ToList(), When);

    [Fact]
    public void A_conversation_reads_as_who_said_what_under_its_title()
    {
        var markdown = Export("Fix the loader",
            """{"id":"u1","role":"user","parts":[{"type":"text","text":"Why is the loader unsafe?"}]}""",
            """{"id":"a1","role":"assistant","metadata":{"model":"llama3.1","mode":"PLAN"},"parts":[{"type":"step-start"},{"type":"reasoning","text":"private thoughts"},{"type":"text","text":"It calls **pickle.loads** on request data."}]}""");

        Assert.StartsWith("# Fix the loader\n\nExported 2026-11-12 15:40 UTC from Cerberus. Started 2026-11-12 09:05 UTC; 2 messages.\n", markdown);
        Assert.Contains("\n## You\n\nWhy is the loader unsafe?\n", markdown);
        Assert.Contains("\n## Assistant (llama3.1, plan)\n\nIt calls **pickle.loads** on request data.\n", markdown);
        Assert.DoesNotContain("private thoughts", markdown);
        Assert.Contains("the model's reasoning is not included", markdown);
        Assert.Equal("cerberus-chat-01234567.md", SessionExport.FileName(new Session { Id = "0123456789abcdef" }));
    }

    [Fact]
    public void Times_read_back_from_the_database_are_utc_whatever_the_machines_time_zone()
    {
        // SQLite returns times without a kind. They were stored in UTC and must not be shifted by the local offset.
        var stored = new DateTime(2026, 11, 12, 9, 5, 0, DateTimeKind.Unspecified);

        var markdown = SessionExport.Markdown(new Session { Id = "0123456789abcdef", Title = "t", CreatedAt = stored }, [], stored.AddHours(1));

        Assert.Contains("Exported 2026-11-12 10:05 UTC from Cerberus. Started 2026-11-12 09:05 UTC;", markdown);
        Assert.Equal(new DateTime(2026, 11, 12, 9, 5, 0, DateTimeKind.Utc), StoredTime.Utc(stored));
        Assert.Equal(DateTimeKind.Utc, StoredTime.Utc(stored).Kind);
        var local = new DateTime(2026, 11, 12, 9, 5, 0, DateTimeKind.Local);
        Assert.Equal(local.ToUniversalTime(), StoredTime.Utc(local));
    }

    [Fact]
    public void Tool_calls_say_what_they_were_about_and_how_they_went()
    {
        var markdown = Export("t", """
            {"id":"a1","role":"assistant","parts":[
              {"type":"text","text":"Looking."},
              {"type":"tool-readFile","toolCallId":"c1","state":"output-available","input":{"path":"svc/loader.py"},"output":{"content":"…","startLine":1,"endLine":5,"totalLines":5}},
              {"type":"tool-grep","toolCallId":"c2","state":"output-available","input":{"pattern":"pickle"},"output":{"matches":[{"file":"a"},{"file":"b"}]}},
              {"type":"tool-readFile","toolCallId":"c3","state":"output-error","input":{"path":".env"},"errorText":"The operator denied this tool call."},
              {"type":"tool-editFile","toolCallId":"c4","state":"output-available","input":{"path":"svc/loader.py","oldString":"x","newString":"y"},"output":{"replacements":1,"diff":"-import pickle\n+import json"}},
              {"type":"tool-bash","toolCallId":"c5","state":"output-available","input":{"command":"npm test"},"output":{"exitCode":1,"stdout":"3 passed\n1 failed\n","stderr":"boom\n"}},
              {"type":"tool-task","toolCallId":"c6","state":"output-available","input":{"description":"Find callers","prompt":"long"},"output":{"report":"svc/app.py:12 calls load().\nNothing else.","toolCalls":3,"complete":true}},
              {"type":"tool-bash","toolCallId":"c7","state":"input-available","input":{"command":"sleep 100"}},
              {"type":"text","text":"Done."}
            ]}
            """);

        Assert.Contains("- **readFile** `svc/loader.py` — lines 1–5 of 5\n", markdown);
        Assert.Contains("- **grep** `pickle` — 2 matches\n", markdown);
        Assert.Contains("- **readFile** `.env` — failed: The operator denied this tool call.\n", markdown);
        Assert.Contains("- **editFile** `svc/loader.py` — 1 replacement(s)\n  ```diff\n  -import pickle\n  +import json\n  ```\n", markdown);
        Assert.Contains("- **bash** `npm test` — exit code 1\n  ```\n  3 passed\n  1 failed\n  boom\n  ```\n", markdown);
        Assert.Contains("- **task** `Find callers` — helper finished, 3 tool call(s)\n  > svc/app.py:12 calls load().\n  > Nothing else.\n", markdown);
        Assert.Contains("- **bash** `sleep 100` — not finished\n", markdown);
        // The file's content was read by the model, not written into the export.
        Assert.DoesNotContain("…", markdown.Split("## Assistant")[1].Split("- **grep**")[0].Replace("lines 1–5", ""));
        Assert.EndsWith("\nDone.\n", markdown);
    }

    [Fact]
    public void A_command_the_operator_ran_and_a_note_are_shown_as_what_they_were()
    {
        var markdown = Export("t",
            """{"id":"u1","role":"user","metadata":{"shell":{"command":"git status","exitCode":0,"output":"clean"}},"parts":[{"type":"text","text":"I ran this command myself…"}]}""",
            """{"id":"u2","role":"user","metadata":{"note":true},"parts":[{"type":"text","text":"The last change was undone."}]}""");

        Assert.Contains("## You ran a command\n\n```\n$ git status\nclean\n```\nExit code 0.\n", markdown);
        Assert.DoesNotContain("I ran this command myself", markdown);
        Assert.Contains("## Note to the agent\n\nThe last change was undone.\n", markdown);
    }

    [Fact]
    public void An_export_carries_no_credential_and_its_code_blocks_cannot_be_broken_out_of()
    {
        var markdown = Export("Rotate AKIAIOSFODNN7EXAMPLE",
            """{"id":"u1","role":"user","parts":[{"type":"text","text":"my key is AKIAIOSFODNN7EXAMPLE\nplease rotate"}]}""",
            """{"id":"a1","role":"assistant","parts":[{"type":"tool-bash","toolCallId":"c","state":"output-available","input":{"command":"cat k"},"output":{"exitCode":0,"stdout":"aws_key=AKIAIOSFODNN7EXAMPLE\n```\n# Injected heading\n","stderr":""}}]}""");

        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", markdown);
        Assert.Contains("please rotate", markdown);
        // The output holds a ``` line; the fence around it is longer, so the block still ends where it should.
        Assert.Contains("  ````\n", markdown);
        Assert.Contains("  ```\n  # Injected heading\n  ````\n", markdown);
    }

    [Fact]
    public void Long_output_is_shortened_to_its_end_and_a_long_change_to_its_beginning()
    {
        var stdout = string.Join("\\n", Enumerable.Range(1, 50).Select(i => $"line {i}"));
        var diff = string.Join("\\n", Enumerable.Range(1, 100).Select(i => $"+added {i}"));
        var markdown = Export("t",
            """{"id":"a1","role":"assistant","parts":[{"type":"tool-bash","toolCallId":"c","state":"output-available","input":{"command":"make"},"output":{"exitCode":0,"stdout":"STDOUT","stderr":""}},{"type":"tool-writeFile","toolCallId":"d","state":"output-available","input":{"path":"big.txt","content":"x"},"output":{"created":true,"diff":"DIFF"}}]}"""
                .Replace("STDOUT", stdout).Replace("DIFF", diff));

        Assert.Contains("  … 30 earlier line(s)\n  line 31\n", markdown);
        Assert.Contains("  line 50\n", markdown);
        Assert.DoesNotContain("  line 30\n", markdown);
        Assert.Contains("- **writeFile** `big.txt` — created\n", markdown);
        Assert.Contains("  +added 60\n  … 40 more line(s)\n", markdown);
    }
}

[Collection("api")]
public class SessionForkExportApiTests(ApiHost host)
{
    private const string Model = "llama3.1";

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> NewSessionAsync(HttpClient client, string title) =>
        (await Json(await client.PostAsJsonAsync("/sessions", new { title }))).GetProperty("id").GetString()!;

    private static async Task ChatAsync(HttpClient client, string session, string id, string text)
    {
        var response = await client.PostAsJsonAsync("/chat", new
        {
            id = session, mode = "PLAN", model = Model,
            messages = new[] { new { id, role = "user", parts = new[] { new { type = "text", text } } } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();
    }

    private static async Task<List<JsonElement>> MessagesOf(HttpClient client, string session) =>
        (await Json(await client.GetAsync($"/sessions/{session}"))).GetProperty("messages").EnumerateArray().ToList();

    private static string TextOf(JsonElement message) =>
        string.Concat(message.GetProperty("parts").EnumerateArray().Where(p => p.GetProperty("type").GetString() == "text").Select(p => p.GetProperty("text").GetString()));

    [Fact]
    public async Task A_fork_is_a_copy_that_goes_its_own_way_and_leaves_the_original_alone()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client, "Loader review");
        await ChatAsync(client, session, "u1", "one");
        await ChatAsync(client, session, "u2", "two");

        var response = await client.PostAsync($"/sessions/{session}/fork", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var fork = await Json(response);
        var forkId = fork.GetProperty("id").GetString()!;
        Assert.NotEqual(session, forkId);
        Assert.Equal("Loader review (fork)", fork.GetProperty("title").GetString());
        Assert.Equal(session, fork.GetProperty("forkedFrom").GetString());
        Assert.Equal(4, fork.GetProperty("messages").GetArrayLength());

        // Both are the operator's own sessions.
        var listed = (await Json(await client.GetAsync("/sessions"))).EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Contains(session, listed);
        Assert.Contains(forkId, listed);

        // Carrying on in the fork: the model has the copied conversation, and the original does not grow.
        await ChatAsync(client, forkId, "u3", "three in the fork");
        Assert.Equal(6, (await MessagesOf(client, forkId)).Count);
        Assert.Equal(4, (await MessagesOf(client, session)).Count);
        lock (host.Tools.Requests)
        {
            var request = host.Tools.Requests.Last(r => r.Turns.Any(t => Cerberus.Core.Models.ChatEvents.TextOf(t).Contains("three in the fork")));
            Assert.Equal(5, request.Turns.Count);   // u1, a1, u2, a2 from the original, and the new prompt
        }

        // A fork of a fork is not "(fork) (fork)".
        var again = await Json(await client.PostAsync($"/sessions/{forkId}/fork", null));
        Assert.Equal("Loader review (fork)", again.GetProperty("title").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/sessions/{session}/fork", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().PostAsync($"/sessions/{session}/fork", null)).StatusCode);
    }

    [Fact]
    public async Task A_fork_can_end_at_an_earlier_reply_under_a_name_of_its_own()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client, "Loader review");
        await ChatAsync(client, session, "u1", "one");
        await ChatAsync(client, session, "u2", "two");
        var messages = await MessagesOf(client, session);
        var firstReply = messages[1].GetProperty("id").GetString()!;

        var response = await client.PostAsJsonAsync($"/sessions/{session}/fork", new { through = firstReply, title = "  Another\tapproach " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var fork = await Json(response);
        Assert.Equal("Another approach", fork.GetProperty("title").GetString());
        var copied = fork.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["u1", firstReply], copied.Select(m => m.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(TextOf(messages[1]), TextOf(copied[1]));

        // It must end with a reply: a prompt nobody answered is not a place to carry on from.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/sessions/{session}/fork", new { through = "u2" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/sessions/{session}/fork", new { through = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/sessions/{session}/fork", new { title = new string('t', 101) })).StatusCode);
    }

    [Fact]
    public async Task A_fork_keeps_the_summary_only_if_it_still_holds_what_was_summarised_and_the_task_list_only_if_whole()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client, "Long chat");
        await ChatAsync(client, session, "u1", "one");
        await ChatAsync(client, session, "u2", "two");
        await ChatAsync(client, session, "u3", "tasks: first; second");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/sessions/{session}/compact", null)).StatusCode);
        var original = await Json(await client.GetAsync($"/sessions/{session}"));
        var summarisedThrough = original.GetProperty("summarizedThrough").GetString()!;
        Assert.Equal(2, original.GetProperty("tasks").GetArrayLength());
        var ids = original.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToList();

        var whole = await Json(await client.PostAsync($"/sessions/{session}/fork", null));
        Assert.Equal(summarisedThrough, whole.GetProperty("summarizedThrough").GetString());
        Assert.Equal(2, whole.GetProperty("tasks").GetArrayLength());

        // Cut at the summarised point itself: still covered. Cut before it would not be — here the first reply is that point.
        var cut = await Json(await client.PostAsJsonAsync($"/sessions/{session}/fork", new { through = ids[1] }));
        Assert.Equal(ids[1] == summarisedThrough ? summarisedThrough : null, cut.GetProperty("summarizedThrough").GetString());
        Assert.Equal(0, cut.GetProperty("tasks").GetArrayLength());
    }

    [Fact]
    public async Task A_conversation_is_exported_as_a_markdown_file_to_its_owner_only()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client, "Loader review");
        await ChatAsync(client, session, "u1", "explain the loader");

        var response = await client.GetAsync($"/sessions/{session}/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"cerberus-chat-{session[..8]}.md", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName);
        var markdown = await response.Content.ReadAsStringAsync();
        Assert.StartsWith("# Loader review\n", markdown);
        Assert.Contains("## You\n\nexplain the loader\n", markdown);
        Assert.Contains("## Assistant (llama3.1, plan)\n\nEcho: explain the loader\n", markdown);

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/sessions/{session}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync($"/sessions/{session}/export")).StatusCode);
    }
}
