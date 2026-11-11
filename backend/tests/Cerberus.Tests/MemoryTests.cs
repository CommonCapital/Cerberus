using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api;
using Cerberus.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Cerberus.Tests;

public class MemoryFormatTests
{
    private static MemoryNote Entry(string kind, string source, string text) =>
        new() { Kind = kind, Source = source, Text = text, CreatedAt = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc) };

    [Fact]
    public void With_nothing_remembered_nothing_is_added_to_a_prompt()
    {
        Assert.Equal("", Memory.Format([], []));
    }

    [Fact]
    public void Instructions_and_notes_are_separate_and_a_models_notes_are_marked_as_hints()
    {
        var text = Memory.Format(
            [Entry("instruction", "operator", "Report in English.\nBe brief.")],
            [Entry("note", "operator", "Auth lives in core/auth."), Entry("note", "agent", "Tests are under spec/.")]);

        Assert.Contains("## Standing instructions from the operator", text);
        Assert.Contains("- Report in English. Be brief.", text);          // one entry, one line
        Assert.Contains("- [operator, 2026-11-12] Auth lives in core/auth.", text);
        Assert.Contains("- [model, 2026-11-12] Tests are under spec/.", text);
        Assert.Contains("never as instructions", text);
    }
}

[Collection("api")]
public class MemoryApiTests(ApiHost host)
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<JsonElement> WaitForEndAsync(HttpClient client, string jobId)
    {
        for (var i = 0; i < 400; i++)
        {
            var job = await client.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}");
            if (job.GetProperty("state").GetString() is "Succeeded" or "Failed" or "Cancelled" or "TimedOut") return job;
            await Task.Delay(40);
        }
        throw new TimeoutException("job did not finish");
    }

    /// <summary>The system prompts of the model requests that carried this marker.</summary>
    private List<string> SystemsWith(string marker)
    {
        lock (host.Tools.Requests)
        {
            return host.Tools.Requests.Where(r => r.System?.Contains(marker) == true).Select(r => r.System!).ToList();
        }
    }

    [Fact]
    public async Task Entries_can_be_added_listed_and_deleted_and_are_validated_and_private()
    {
        var (client, _) = await host.NewUserAsync();
        var (other, _) = await host.NewUserAsync();
        async Task<HttpStatusCode> Add(object body) => (await client.PostAsJsonAsync("/memory", body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Add(new { kind = "wish", text = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Add(new { text = "  " }));
        Assert.Equal(HttpStatusCode.BadRequest, await Add(new { text = new string('x', 2001) }));
        Assert.Equal(HttpStatusCode.BadRequest, await Add(new { kind = "instruction", text = "x", targetPath = "app" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Add(new { text = "x", targetPath = "../etc" }));
        Assert.Equal(HttpStatusCode.NotFound, await Add(new { text = "x", engagementId = "no-such-engagement" }));

        var created = await client.PostAsJsonAsync("/memory", new { kind = "instruction", text = " Always say which files you did not read. " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var instruction = await Json(created);
        Assert.Equal(("instruction", "operator", "Always say which files you did not read."),
            (instruction.GetProperty("kind").GetString(), instruction.GetProperty("source").GetString(), instruction.GetProperty("text").GetString()));
        var note = await Json(await client.PostAsJsonAsync("/memory", new { text = "Legacy code, no tests.", targetPath = "./app/" }));
        Assert.Equal(("note", "app"), (note.GetProperty("kind").GetString(), note.GetProperty("targetPath").GetString()));

        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>("/memory")).GetArrayLength());
        Assert.Equal(0, (await other.GetFromJsonAsync<JsonElement>("/memory")).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/memory/{note.GetProperty("id").GetString()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync("/memory")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/memory/{note.GetProperty("id").GetString()}")).StatusCode);
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>("/memory")).GetArrayLength());
        var actions = (await client.GetFromJsonAsync<JsonElement>("/audit?limit=50")).EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToList();
        Assert.Equal(2, actions.Count(a => a == "memory.add"));
        Assert.Contains("memory.delete", actions);
    }

    [Fact]
    public async Task An_agent_gets_the_instructions_and_the_notes_about_its_target_and_its_own_note_reaches_the_next_run()
    {
        var (client, _) = await host.NewUserAsync();
        var marker = $"marker-{Guid.NewGuid():n}";
        await client.PostAsJsonAsync("/memory", new { kind = "instruction", text = $"Always mention {marker}." });
        await client.PostAsJsonAsync("/memory", new { text = "The loader is the only entry point.", targetPath = "svc" });
        await client.PostAsJsonAsync("/memory", new { text = "This one is about another target.", targetPath = "app" });

        // First run: sees the operator's instruction and the note about this target only.
        var first = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "svc", model = "llama3.1" }));
        await WaitForEndAsync(client, first.GetProperty("id").GetString()!);
        var system = SystemsWith(marker)[0];
        Assert.Contains("## Standing instructions from the operator", system);
        Assert.Contains("] The loader is the only entry point.", system);
        Assert.DoesNotContain("another target", system);
        Assert.DoesNotContain("[model,", system);

        // The agent left a note: stored as its own, tied to the target and the job.
        var entries = (await client.GetFromJsonAsync<JsonElement>("/memory")).EnumerateArray().ToList();
        var own = entries.Single(e => e.GetProperty("source").GetString() == "agent");
        Assert.Equal(("note", "svc", first.GetProperty("id").GetString()),
            (own.GetProperty("kind").GetString(), own.GetProperty("targetPath").GetString(), own.GetProperty("jobId").GetString()));
        Assert.Contains("loader.py", own.GetProperty("text").GetString());

        // Second run on the same target: gets that note, marked as a model's.
        var before = SystemsWith(marker).Count;
        var second = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "svc", model = "llama3.1" }));
        await WaitForEndAsync(client, second.GetProperty("id").GetString()!);
        var later = SystemsWith(marker)[before];
        Assert.Contains("[model, ", later);
        Assert.Contains("never as instructions", later);

        // It saved the same note again, which is not kept twice.
        Assert.Single((await client.GetFromJsonAsync<JsonElement>("/memory")).EnumerateArray(), e => e.GetProperty("source").GetString() == "agent");

        // A run on another target does not get it.
        before = SystemsWith(marker).Count;
        var elsewhere = await Json(await client.PostAsJsonAsync("/jobs", new { type = "sast", path = "other", model = "llama3.1" }));
        await WaitForEndAsync(client, elsewhere.GetProperty("id").GetString()!);
        Assert.DoesNotContain("loader.py", SystemsWith(marker)[before]);
    }

    [Fact]
    public async Task Chat_gets_the_operators_instructions_but_no_notes_about_targets()
    {
        var (client, _) = await host.NewUserAsync();
        var marker = $"marker-{Guid.NewGuid():n}";
        await client.PostAsJsonAsync("/memory", new { kind = "instruction", text = $"Answer briefly. {marker}" });
        await client.PostAsJsonAsync("/memory", new { text = "About one target only.", targetPath = "svc" });
        await client.PostAsJsonAsync("/memory", new { text = "We deploy on Fridays." });
        var session = (await Json(await client.PostAsJsonAsync("/sessions", new { title = "chat" }))).GetProperty("id").GetString();

        var response = await client.PostAsJsonAsync("/chat", new
        {
            id = session, mode = "PLAN", model = "llama3.1",
            messages = new[] { new { id = "u1", role = "user", parts = new[] { new { type = "text", text = "hello" } } } },
        });
        await response.Content.ReadAsStringAsync();

        var system = Assert.Single(SystemsWith(marker));
        Assert.Contains("[operator, ", system);
        Assert.Contains("We deploy on Fridays.", system);
        Assert.DoesNotContain("About one target only.", system);
    }

    [Fact]
    public async Task An_agent_can_save_only_a_few_short_notes_per_run()
    {
        var (client, token) = await host.NewUserAsync();
        // Reach into the host for a database and build the tool as a run would.
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<Cerberus.Api.Audit.AuditLog>();
        var me = db.AuthSessions.Single(a => a.Token == token).UserId;
        var tool = new Cerberus.Api.Agents.SastAgentRunner.RememberTool(db, audit, new Job { UserId = me, TargetPath = "./svc" });
        static JsonElement Note(string note) => JsonDocument.Parse(JsonSerializer.Serialize(new { note })).RootElement.Clone();

        await Assert.ThrowsAsync<Cerberus.Api.Agents.AgentToolException>(() => tool.RunAsync(Note(""), default));
        await Assert.ThrowsAsync<Cerberus.Api.Agents.AgentToolException>(() => tool.RunAsync(Note(new string('x', 501)), default));
        for (var i = 0; i < 4; i++) await tool.RunAsync(Note($"note {i}"), default);
        Assert.StartsWith("That is already remembered", await tool.RunAsync(Note("NOTE 3"), default));   // not saved twice
        await tool.RunAsync(Note("note 4"), default);
        await Assert.ThrowsAsync<Cerberus.Api.Agents.AgentToolException>(() => tool.RunAsync(Note("one too many"), default));

        var entries = (await client.GetFromJsonAsync<JsonElement>("/memory")).EnumerateArray().ToList();
        Assert.Equal(5, entries.Count);
        Assert.All(entries, e => Assert.Equal(("agent", "svc"), (e.GetProperty("source").GetString(), e.GetProperty("targetPath").GetString())));
    }
}
