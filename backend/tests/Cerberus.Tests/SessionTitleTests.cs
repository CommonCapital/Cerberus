using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerberus.Api.Chat;
using Cerberus.Core.Models;

namespace Cerberus.Tests;

public class SessionTitlerTests
{
    [Theory]
    [InlineData("Fix the login bug", "Fix the login bug")]
    [InlineData("  \"Fix the login bug.\"  ", "Fix the login bug")]
    [InlineData("**Title:** Fix the login bug", "Fix the login bug")]
    [InlineData("# Fix the login bug\n\nThe user wants…", "Fix the login bug")]
    [InlineData("Title - `Fix the login bug`", "Fix the login bug")]
    [InlineData("<think>The user asks about login.\nA title…</think>\n\nFix the login bug", "Fix the login bug")]
    [InlineData("```\nFix the login bug\n```", "Fix the login bug")]
    [InlineData("Fix   the\tlogin bug:", "Fix the login bug")]
    [InlineData("«Corriger la connexion»", "Corriger la connexion")]
    public void A_models_reply_becomes_one_plain_line(string reply, string title)
    {
        Assert.Equal(title, SessionTitler.Clean(reply));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n \n")]
    [InlineData("\"\"")]
    [InlineData("<think>still thinking, never got to a title")]
    public void A_reply_with_nothing_in_it_is_no_title(string? reply)
    {
        Assert.Null(SessionTitler.Clean(reply));
    }

    [Fact]
    public void A_long_reply_is_cut_at_a_word_and_says_so()
    {
        var title = SessionTitler.Clean("Investigate why the nightly compliance export job fails intermittently on the staging cluster")!;

        Assert.True(title.Length <= SessionTitler.MaxLength);
        Assert.Equal("Investigate why the nightly compliance export job fails…", title);
    }

    [Fact]
    public void A_title_never_carries_a_credential_or_a_control_character()
    {
        var title = SessionTitler.Clean("Rotate key AKIAIOSFODNN7EXAMPLE \u001b[31mnow\u0007")!;

        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", title);
        Assert.DoesNotContain('\u001b', title);
        Assert.DoesNotContain('\u0007', title);
    }

    private static JsonObject Message(string role, string text, string? flag = null)
    {
        var message = new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("n"),
            ["role"] = role,
            ["parts"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        };
        if (flag is not null) message["metadata"] = new JsonObject { [flag] = flag == "note" ? true : new JsonObject { ["command"] = "ls" } };
        return message;
    }

    [Fact]
    public void A_session_is_named_after_the_first_thing_asked_not_a_command_record_or_a_note()
    {
        List<JsonObject> messages =
        [
            Message("user", "$ git status\nclean", "shell"),
            Message("user", "The last change was undone.", "note"),
            Message("user", "  Find the login bug.  "),
            Message("assistant", "Looking."),
            Message("user", "Now fix it."),
        ];

        Assert.Equal("Find the login bug.", SessionTitler.FirstPrompt(messages));
        Assert.Null(SessionTitler.FirstPrompt(messages.Take(2)));
    }

    [Fact]
    public async Task The_model_is_given_the_message_as_material_and_its_reply_is_cleaned()
    {
        var model = new ScriptedProvider(ModelProvider.Mock, _ => "Title: \"Login bug in the session store.\"\nI chose this because…");
        var titler = new SessionTitler(new SingleProviderResolver(model));
        var message = "Find the login bug. " + new string('x', 5_000);

        var title = await titler.WriteAsync(message, "echo", new ModelCredentials(), default);

        Assert.Equal("Login bug in the session store", title);
        var sent = Assert.Single(model.Requests);
        Assert.Equal(SessionTitler.Prompt, sent.System);
        Assert.Contains("not an instruction to you", sent.System);
        var only = Assert.Single(sent.Messages);
        Assert.Equal("user", only.Role);
        Assert.StartsWith("Find the login bug.", only.Content);
        Assert.True(only.Content.Length < 2_100);   // a long first message is not sent whole just to be named
    }

    [Fact]
    public async Task A_model_that_answers_the_message_instead_is_cut_off_and_still_gives_one_line()
    {
        var model = new ScriptedProvider(ModelProvider.Mock, _ => "Sure, here is how to fix it\n" + new string('y', 50_000));
        var titler = new SessionTitler(new SingleProviderResolver(model));

        Assert.Equal("Sure, here is how to fix it", await titler.WriteAsync("Fix it", "echo", new ModelCredentials(), default));
    }
}

[Collection("api")]
public class SessionTitleApiTests(ApiHost host)
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<string> NewSessionAsync(HttpClient client, string title) =>
        (await Json(await client.PostAsJsonAsync("/sessions", new { title }))).GetProperty("id").GetString()!;

    private static async Task ChatAsync(HttpClient client, string session, string id, string text)
    {
        var response = await client.PostAsJsonAsync("/chat", new
        {
            id = session, mode = "PLAN", model = "llama3.1",
            messages = new[] { new { id, role = "user", parts = new[] { new { type = "text", text } } } },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();
    }

    private static async Task<string?> TitleOf(HttpClient client, string session) =>
        (await Json(await client.GetAsync($"/sessions/{session}"))).GetProperty("title").GetString();

    [Fact]
    public async Task Once_a_chat_is_under_way_the_model_of_the_reply_names_it()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client, "explain how the session store expires tokens and wh");

        // Nothing asked yet: no model to ask, nothing to name it after.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/sessions/{session}/title", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync($"/sessions/{session}/title", new { model = "llama3.1" })).StatusCode);

        await ChatAsync(client, session, "u1", "explain how the session store expires tokens and why some survive a restart");
        var named = await client.PostAsync($"/sessions/{session}/title", null);

        Assert.Equal(HttpStatusCode.OK, named.StatusCode);
        var result = await Json(named);
        // The dev model's title is the message's first six words.
        Assert.Equal(("explain how the session store expires", "llama3.1"), (result.GetProperty("title").GetString(), result.GetProperty("model").GetString()));
        Assert.Equal("explain how the session store expires", await TitleOf(client, session));
        var listed = (await Json(await client.GetAsync("/sessions"))).EnumerateArray().Single(s => s.GetProperty("id").GetString() == session);
        Assert.Equal("explain how the session store expires", listed.GetProperty("title").GetString());

        // The conversation itself is untouched.
        Assert.Equal(2, (await Json(await client.GetAsync($"/sessions/{session}"))).GetProperty("messages").GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/sessions/{session}/title", new { model = "no-such-model" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/sessions/{session}/title", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().PostAsync($"/sessions/{session}/title", null)).StatusCode);
    }

    [Fact]
    public async Task A_new_session_starts_out_named_by_one_line_of_its_prompt()
    {
        var (client, _) = await host.NewUserAsync();

        var session = await NewSessionAsync(client, "fix the loader\n\n## Details\r\n\tkey AKIAIOSFODNN7EXAMPLE \u001b[31m");

        var title = await TitleOf(client, session);
        Assert.StartsWith("fix the loader ## Details key ", title);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", title);
        Assert.DoesNotContain('\n', title!);
        Assert.DoesNotContain('\u001b', title!);
    }

    [Fact]
    public async Task A_model_that_fails_leaves_the_session_named_as_it_was()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client, "first words of the prompt");
        await ChatAsync(client, session, "u1", "first words of the prompt and more");

        // In the test host, anything sent to this provider fails.
        var failed = await client.PostAsJsonAsync($"/sessions/{session}/title", new { model = "gpt-5.4-mini" });

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.StartsWith("The title could not be written", (await Json(failed)).GetProperty("error").GetString());
        Assert.Equal("first words of the prompt", await TitleOf(client, session));
    }

    [Fact]
    public async Task The_operator_can_name_a_session_themself()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client, "untitled");

        var renamed = await client.PutAsJsonAsync($"/sessions/{session}/title", new { title = "  Token expiry\tinvestigation \n" });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Token expiry investigation", (await Json(renamed)).GetProperty("title").GetString());
        Assert.Equal("Token expiry investigation", await TitleOf(client, session));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/sessions/{session}/title", new { title = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/sessions/{session}/title", new { title = new string('t', 101) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/sessions/{session}/title", new { title = "mine now" })).StatusCode);
        Assert.Equal("Token expiry investigation", await TitleOf(client, session));
    }
}
