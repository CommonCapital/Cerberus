using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Core.Models;

namespace Cerberus.Tests;

[Collection("api")]
public class ShellRecordApiTests(ApiHost host)
{
    [Fact]
    public async Task A_prompt_can_bring_along_the_record_of_a_command_the_operator_ran()
    {
        var (client, _) = await host.NewUserAsync();
        var session = (await (await client.PostAsJsonAsync("/sessions", new { title = "chat" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var marker = $"m{Guid.NewGuid():n}";
        var shell = new { command = "npm test", exitCode = 1, output = "1 failed" };

        // As the CLI sends it: the command's record first, then the prompt, in one request.
        var response = await client.PostAsJsonAsync("/chat", new
        {
            id = session, mode = "BUILD", model = "llama3.1",
            messages = new object[]
            {
                new { id = "s1", role = "user", parts = new[] { new { type = "text", text = "I ran this command myself in the project directory:\n$ npm test\n\n1 failed\n\n(exit code 1)" } }, metadata = new { shell } },
                new { id = "u1", role = "user", parts = new[] { new { type = "text", text = $"why did it fail {marker}" } } },
            },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await response.Content.ReadAsStringAsync();

        // The model was told both, in order, as the user's words.
        ChatRequest request;
        lock (host.Tools.Requests) request = host.Tools.Requests.Last(r => r.Turns.Any(t => ChatEvents.TextOf(t).Contains(marker)));
        Assert.Equal(["user", "user"], request.Turns.Select(t => t.Role).ToArray());
        Assert.Contains("$ npm test", ChatEvents.TextOf(request.Turns[0]));
        Assert.Contains($"why did it fail {marker}", ChatEvents.TextOf(request.Turns[1]));

        // Both are stored, the record with what the client needs to show it again.
        var stored = (await client.GetFromJsonAsync<JsonElement>($"/sessions/{session}")).GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["s1", "u1"], stored.Take(2).Select(m => m.GetProperty("id").GetString()!).ToArray());
        var kept = stored[0].GetProperty("metadata");
        Assert.Equal(("npm test", 1), (kept.GetProperty("shell").GetProperty("command").GetString(), kept.GetProperty("shell").GetProperty("exitCode").GetInt32()));
        Assert.Equal("BUILD", kept.GetProperty("mode").GetString());   // stamped like any incoming message
        Assert.Equal("assistant", stored[2].GetProperty("role").GetString());
    }
}
