using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cerberus.Api.Chat;
using Cerberus.Api.Jobs;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerberus.Tests;

public class WorkMessageTests
{
    [Fact]
    public async Task Work_is_said_to_be_done_once_however_often_it_is_said()
    {
        var acknowledged = 0;
        var delivery = new Delivery(new WorkMessage("job-1", "user-1"), () =>
        {
            acknowledged++;
            return ValueTask.CompletedTask;
        });

        await delivery.DoneAsync();
        await delivery.DoneAsync();
        await new Delivery(new WorkMessage("job-2", "user-1")).DoneAsync();   // a queue with nothing to acknowledge

        Assert.Equal(1, acknowledged);
    }

    [Fact]
    public void A_message_on_the_broker_is_two_ids_and_anything_else_is_not_a_message()
    {
        var body = RabbitMqBus.Encode(new WorkMessage("turn-1", "user-7"));

        Assert.Equal("""{"id":"turn-1","userId":"user-7"}""", Encoding.UTF8.GetString(body));
        Assert.Equal(new WorkMessage("turn-1", "user-7"), RabbitMqBus.Decode(body));
        // More fields are ignored — never trusted: the work's details come from the database.
        Assert.Equal(new WorkMessage("a", "b"), RabbitMqBus.Decode("""{"id":"a","userId":"b","model":"gpt","admin":true}"""u8));

        foreach (var junk in new[]
                 {
                     "", "not json", "[]", "\"turn-1\"", "{}", """{"id":"a"}""", """{"userId":"b"}""", """{"id":"","userId":"b"}""",
                     """{"id":7,"userId":"b"}""", """{"id":"a","userId":null}""",
                     $$"""{"id":"{{new string('x', RabbitMqBus.MaxIdLength + 1)}}","userId":"b"}""",
                 })
        {
            Assert.Null(RabbitMqBus.Decode(Encoding.UTF8.GetBytes(junk)));
        }
    }

    [Fact]
    public void Where_the_broker_is_can_be_said_without_its_password()
    {
        var options = new RabbitMqOptions("amqps://cerberus:made-up-secret@queue.internal.example:5671/prod");

        Assert.True(options.Enabled);
        Assert.Equal("queue.internal.example:5671", options.Where);
        Assert.DoesNotContain("made-up-secret", options.Where);
        Assert.Equal("localhost:5672", new RabbitMqOptions("amqp://localhost").Where);
        Assert.False(new RabbitMqOptions(null).Enabled);
        Assert.False(new RabbitMqOptions("  ").Enabled);
    }

    [Fact]
    public async Task A_broker_that_is_away_loses_nothing_and_stops_nothing()
    {
        // Nothing listens on this port: the broker is "down".
        await using var bus = new RabbitMqBus(new RabbitMqOptions("amqp://guest:guest@127.0.0.1:1/"), NullLogger<RabbitMqBus>.Instance);

        // Publishing does not throw — the work is stored as waiting, and the message is kept to be tried again.
        await bus.PublishAsync(WorkQueues.Jobs, new WorkMessage("job-1", "user-1"));
        await bus.PublishAsync(WorkQueues.ChatTurns, new WorkMessage("turn-1", "user-1"));

        Assert.Equal(2, bus.Parked);
        Assert.Equal("RabbitMQ", bus.Transport);
        Assert.Equal("the broker cannot be reached", await bus.ProblemAsync(default));

        // A consumer waits for the broker instead of failing, and stops when asked.
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var received = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in bus.ConsumeAsync(WorkQueues.Jobs, stop.Token)) received++;
        });
        Assert.Equal(0, received);
    }
}

[Collection("api")]
public class ChatTurnApiTests(ApiHost host)
{
    private const string Model = "llama3.1";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static object UserMessage(string id, string text) => new { id, role = "user", parts = new[] { new { type = "text", text } } };

    private static async Task<string> NewSessionAsync(HttpClient client) =>
        (await Json(await client.PostAsJsonAsync("/sessions", new { title = "chat" }))).GetProperty("id").GetString()!;

    private static async Task<JsonElement> SubmitAsync(HttpClient client, string sessionId, params object[] messages)
    {
        var response = await client.PostAsJsonAsync("/chat/turns", new { id = sessionId, messages, mode = "BUILD", model = Model });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return await Json(response);
    }

    private static async Task<JsonElement> WaitForAsync(HttpClient client, string turnId, params string[] states)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var turn = await client.GetFromJsonAsync<JsonElement>($"/chat/turns/{turnId}");
            if (states.Contains(turn.GetProperty("state").GetString())) return turn;
            Assert.True(DateTime.UtcNow < deadline, $"turn is {turn.GetProperty("state").GetString()}, expected one of {string.Join("/", states)}");
            await Task.Delay(25);
        }
    }

    private static Task<JsonElement> FinishedAsync(HttpClient client, string turnId) => WaitForAsync(client, turnId, "Completed", "Failed", "Cancelled");

    private static async Task<List<JsonElement>> EventsAsync(HttpClient client, string turnId, long after = 0) =>
        [.. (await client.GetFromJsonAsync<JsonElement>($"/chat/turns/{turnId}/events?after={after}")).GetProperty("events").EnumerateArray()];

    private static string TextOf(IEnumerable<JsonElement> events) => string.Concat(events
        .Select(e => e.GetProperty("chunk"))
        .Where(c => c.GetProperty("type").GetString() == "text-delta")
        .Select(c => c.GetProperty("delta").GetString()));

    private static async Task<List<JsonElement>> StoredAsync(HttpClient client, string sessionId) =>
        (await client.GetFromJsonAsync<JsonElement>($"/sessions/{sessionId}")).GetProperty("messages").EnumerateArray().ToList();

    [Fact]
    public async Task A_turn_is_accepted_queued_answered_and_its_reply_kept_in_order()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client);

        // 1. Accepted at once: the answer is not the response.
        var accepted = await SubmitAsync(client, session, UserMessage("u1", "hello"));
        var turnId = accepted.GetProperty("turnId").GetString()!;
        Assert.Equal((session, 0), (accepted.GetProperty("sessionId").GetString(), accepted.GetProperty("ahead").GetInt32()));
        Assert.Contains(accepted.GetProperty("state").GetString(), new[] { "Queued", "Running", "Completed" });

        // 2. Answered by the worker: the same reply the direct endpoint gives, as numbered pieces.
        var turn = await FinishedAsync(client, turnId);
        Assert.Equal(("Completed", true), (turn.GetProperty("state").GetString(), turn.GetProperty("replyStored").GetBoolean()));
        var events = await EventsAsync(client, turnId);
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.GetProperty("sequence").GetInt64()));
        Assert.Equal(turn.GetProperty("lastSequence").GetInt64(), events[^1].GetProperty("sequence").GetInt64());
        var types = events.Select(e => e.GetProperty("chunk").GetProperty("type").GetString()!).ToList();
        Assert.Equal(["start", "start-step", "text-start"], types.Take(3));
        Assert.Equal(["text-end", "finish-step", "finish"], types.TakeLast(3));
        Assert.Equal("Echo: hello ", TextOf(events));

        // 3. Read from the middle, a client gets exactly what it lacks.
        var tail = await EventsAsync(client, turnId, after: 3);
        Assert.Equal(events.Skip(3).Select(e => e.GetRawText()), tail.Select(e => e.GetRawText()));

        // 4. And the conversation is the user's, stored as with the direct endpoint.
        var stored = await StoredAsync(client, session);
        Assert.Equal(["user", "assistant"], stored.Select(m => m.GetProperty("role").GetString()!).ToArray());
        Assert.Equal(events[0].GetProperty("chunk").GetProperty("messageId").GetString(), stored[1].GetProperty("id").GetString());
        var listed = await client.GetFromJsonAsync<JsonElement>($"/chat/turns?sessionId={session}");
        Assert.Equal(turnId, listed.EnumerateArray().Single().GetProperty("turnId").GetString());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>($"/chat/turns?sessionId={session}&active=true")).GetArrayLength());
    }

    [Fact]
    public async Task The_reply_is_pushed_to_its_user_as_it_is_written_and_to_nobody_else()
    {
        var (alice, aliceToken) = await host.NewUserAsync();
        var (_, bobToken) = await host.NewUserAsync();
        var session = await NewSessionAsync(alice);

        var aliceEvents = new ConcurrentQueue<JsonElement>();
        var aliceTurns = new ConcurrentQueue<JsonElement>();
        var bobHeard = 0;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var aliceHub = host.Hub("/hub/chat", aliceToken);
        await using var bobHub = host.Hub("/hub/chat", bobToken);
        aliceHub.On<JsonElement>("chatEvent", e => aliceEvents.Enqueue(e));
        aliceHub.On<JsonElement>("chatTurn", t =>
        {
            aliceTurns.Enqueue(t);
            if (t.GetProperty("state").GetString() == "Completed") done.TrySetResult();
        });
        bobHub.On<JsonElement>("chatEvent", _ => Interlocked.Increment(ref bobHeard));
        bobHub.On<JsonElement>("chatTurn", _ => Interlocked.Increment(ref bobHeard));
        await aliceHub.StartAsync();
        await bobHub.StartAsync();
        // A connection counts as started once the handshake is done — a moment before the server has put it
        // into its user's group. A real client covers that moment by reading the stored turn; here it is waited out.
        await Task.Delay(250);

        var turnId = (await SubmitAsync(alice, session, UserMessage("u1", "hello"))).GetProperty("turnId").GetString()!;
        await done.Task.WaitAsync(Patience);

        // Every piece, in order, marked with its turn and conversation — what a client needs to rebuild the reply.
        var pushed = aliceEvents.ToList();
        var stored = await EventsAsync(alice, turnId);
        Assert.Equal(stored.Select(e => e.GetProperty("sequence").GetInt64()), pushed.Select(e => e.GetProperty("sequence").GetInt64()));
        Assert.All(pushed, e => Assert.Equal((turnId, session), (e.GetProperty("turnId").GetString(), e.GetProperty("sessionId").GetString())));
        Assert.Equal("Echo: hello ", TextOf(pushed));
        Assert.Equal(["Running", "Completed"], aliceTurns.Select(t => t.GetProperty("state").GetString()!).ToArray());
        // When the end was announced, all of it was already stored.
        Assert.Equal(stored[^1].GetProperty("sequence").GetInt64(), aliceTurns.Last().GetProperty("lastSequence").GetInt64());

        await Task.Delay(150);
        Assert.Equal(0, bobHeard);
    }

    [Fact]
    public async Task A_connection_that_is_not_signed_in_hears_nothing()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client);
        await using var stranger = host.Hub("/hub/chat", "not-a-token");
        var heard = 0;
        stranger.On<JsonElement>("chatEvent", _ => Interlocked.Increment(ref heard));
        // The server drops it: either the start fails or the connection is closed right after.
        try
        {
            await stranger.StartAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or Microsoft.AspNetCore.SignalR.HubException)
        {
            // Refused outright.
        }

        var turnId = (await SubmitAsync(client, session, UserMessage("u1", "hello"))).GetProperty("turnId").GetString()!;
        await FinishedAsync(client, turnId);
        await Task.Delay(150);

        Assert.Equal(0, heard);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().PostAsJsonAsync("/chat/turns", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().GetAsync($"/chat/turns/{turnId}/events")).StatusCode);
    }

    [Fact]
    public async Task One_users_turns_and_conversations_do_not_exist_for_another()
    {
        var (alice, _) = await host.NewUserAsync();
        var (bob, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(alice);
        var turnId = (await SubmitAsync(alice, session, UserMessage("u1", "hello"))).GetProperty("turnId").GetString()!;
        await FinishedAsync(alice, turnId);

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/chat/turns/{turnId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/chat/turns/{turnId}/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.PostAsync($"/chat/turns/{turnId}/cancel", null)).StatusCode);
        Assert.Equal(0, (await bob.GetFromJsonAsync<JsonElement>($"/chat/turns?sessionId={session}")).GetArrayLength());
        // Nor can Bob speak in Alice's conversation — naming her session, or naming her in the body.
        var intruding = await bob.PostAsJsonAsync("/chat/turns", new { id = session, userId = "alice", messages = new[] { UserMessage("x", "hi") }, mode = "BUILD", model = Model });
        Assert.Equal(HttpStatusCode.NotFound, intruding.StatusCode);
        Assert.Equal(2, (await StoredAsync(alice, session)).Count);
    }

    [Fact]
    public async Task What_a_turn_may_ask_is_checked_before_it_is_queued()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client);
        var message = UserMessage("u1", "hi");

        async Task<HttpStatusCode> Post(object body) => (await client.PostAsJsonAsync("/chat/turns", body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { id = session, messages = Array.Empty<object>(), mode = "BUILD", model = Model }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { id = session, messages = new[] { message }, mode = "DESTROY", model = Model }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { id = session, messages = new[] { message }, mode = "BUILD", model = "no-such-model" }));
        Assert.Equal(HttpStatusCode.NotFound, await Post(new { id = "missing", messages = new[] { message }, mode = "BUILD", model = Model }));

        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/chat/turns")).GetArrayLength());   // nothing invalid was queued
    }

    [Fact]
    public async Task Turns_of_one_conversation_are_answered_one_at_a_time_in_the_order_they_were_sent()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        // The model is held back on this conversation's first request only.
        host.Tools.BeforeReply = async request =>
        {
            if (request.Turns.Any(t => Cerberus.Core.Models.ChatEvents.TextOf(t).Contains("first-in-line")) && Interlocked.Increment(ref held) == 1)
            {
                await release.Task;
            }
        };
        try
        {
            var first = (await SubmitAsync(client, session, UserMessage("u1", "first-in-line"))).GetProperty("turnId").GetString()!;
            await WaitForAsync(client, first, "Running");
            var second = await SubmitAsync(client, session, UserMessage("u2", "second"));
            var secondId = second.GetProperty("turnId").GetString()!;
            Assert.Equal(1, second.GetProperty("ahead").GetInt32());   // told where it stands

            // While the first is being answered the second waits — it does not start beside it.
            await Task.Delay(300);
            Assert.Equal("Queued", (await client.GetFromJsonAsync<JsonElement>($"/chat/turns/{secondId}")).GetProperty("state").GetString());
            Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>($"/chat/turns?sessionId={session}&active=true")).GetArrayLength());

            release.SetResult();
            var firstDone = await FinishedAsync(client, first);
            var secondDone = await FinishedAsync(client, secondId);
            Assert.True(secondDone.GetProperty("startedAt").GetDateTime() >= firstDone.GetProperty("finishedAt").GetDateTime());
            // The second reply was written knowing the first exchange.
            Assert.Equal(["user", "assistant", "user", "assistant"], (await StoredAsync(client, session)).Select(m => m.GetProperty("role").GetString()!).ToArray());
        }
        finally
        {
            host.Tools.BeforeReply = null;
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task A_turn_can_be_stopped_while_it_waits_and_while_it_is_answered()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Tools.BeforeReply = async request =>
        {
            if (request.Turns.Any(t => Cerberus.Core.Models.ChatEvents.TextOf(t).Contains("stop-me"))) await release.Task.WaitAsync(Patience);
        };
        try
        {
            var running = (await SubmitAsync(client, session, UserMessage("u1", "stop-me"))).GetProperty("turnId").GetString()!;
            await WaitForAsync(client, running, "Running");
            var waiting = (await SubmitAsync(client, session, UserMessage("u2", "never asked"))).GetProperty("turnId").GetString()!;

            // The one that waits simply never starts.
            var withdrawn = await client.PostAsync($"/chat/turns/{waiting}/cancel", null);
            Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
            Assert.Equal("Cancelled", (await Json(withdrawn)).GetProperty("state").GetString());

            // The one being answered is stopped; how it ended is recorded by the worker.
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync($"/chat/turns/{running}/cancel", null)).StatusCode);
            var stopped = await FinishedAsync(client, running);
            Assert.Equal(("Cancelled", "Stopped by the user.", false),
                (stopped.GetProperty("state").GetString(), stopped.GetProperty("error").GetString(), stopped.GetProperty("replyStored").GetBoolean()));

            // Neither left anything in the conversation, and stopping again changes nothing.
            Assert.Empty(await StoredAsync(client, session));
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/chat/turns/{running}/cancel", null)).StatusCode);
            Assert.Equal("Cancelled", (await client.GetFromJsonAsync<JsonElement>($"/chat/turns/{waiting}")).GetProperty("state").GetString());
        }
        finally
        {
            host.Tools.BeforeReply = null;
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task A_reply_that_stops_at_a_tool_the_client_runs_is_continued_by_the_next_turn()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client);
        var user = UserMessage("u1", "read notes.txt");

        // 1. The model asks for the file; the turn ends there, with nothing stored — the client holds the message.
        var firstId = (await SubmitAsync(client, session, user)).GetProperty("turnId").GetString()!;
        var first = await FinishedAsync(client, firstId);
        Assert.Equal(("Completed", false), (first.GetProperty("state").GetString(), first.GetProperty("replyStored").GetBoolean()));
        var chunks = (await EventsAsync(client, firstId)).Select(e => e.GetProperty("chunk")).ToList();
        var call = chunks.Single(c => c.GetProperty("type").GetString() == "tool-input-available");
        Assert.Equal(("readFile", "notes.txt"), (call.GetProperty("toolName").GetString(), call.GetProperty("input").GetProperty("path").GetString()));
        Assert.Empty(await StoredAsync(client, session));

        // 2. The client ran the tool and sends the message back with the result, as the next turn.
        var assistant = new
        {
            id = chunks[0].GetProperty("messageId").GetString(),
            role = "assistant",
            parts = new object[]
            {
                new { type = "step-start" },
                new { type = "tool-readFile", toolCallId = call.GetProperty("toolCallId").GetString(), state = "output-available", input = new { path = "notes.txt" }, output = new { content = "remember the milk" } },
            },
        };
        var secondId = (await SubmitAsync(client, session, user, assistant)).GetProperty("turnId").GetString()!;
        var second = await FinishedAsync(client, secondId);

        Assert.Equal(("Completed", true), (second.GetProperty("state").GetString(), second.GetProperty("replyStored").GetBoolean()));
        Assert.Equal(["user", "assistant"], (await StoredAsync(client, session)).Select(m => m.GetProperty("role").GetString()!).ToArray());
    }

    [Fact]
    public async Task A_conversation_removed_before_its_turn_is_answered_fails_the_turn_and_says_so()
    {
        var (client, _) = await host.NewUserAsync();
        var blocker = await NewSessionAsync(client);
        var doomed = await NewSessionAsync(client);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Tools.BeforeReply = async request =>
        {
            if (request.Turns.Any(t => Cerberus.Core.Models.ChatEvents.TextOf(t).Contains("hold-the-line"))) await release.Task.WaitAsync(Patience);
        };
        try
        {
            // The user's two places are taken, so the third turn waits — long enough for its conversation to go.
            var a = (await SubmitAsync(client, blocker, UserMessage("u1", "hold-the-line"))).GetProperty("turnId").GetString()!;
            var other = await NewSessionAsync(client);
            var b = (await SubmitAsync(client, other, UserMessage("u1", "hold-the-line"))).GetProperty("turnId").GetString()!;
            await WaitForAsync(client, a, "Running");
            await WaitForAsync(client, b, "Running");
            var turnId = (await SubmitAsync(client, doomed, UserMessage("u1", "hello"))).GetProperty("turnId").GetString()!;
            await Task.Delay(200);
            Assert.Equal("Queued", (await client.GetFromJsonAsync<JsonElement>($"/chat/turns/{turnId}")).GetProperty("state").GetString());   // a third of one user's waits

            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
                db.Sessions.Remove(db.Sessions.Single(s => s.Id == doomed));
                db.SaveChanges();
            }
            release.SetResult();

            var failed = await FinishedAsync(client, turnId);
            Assert.Equal(("Failed", "Session not found"), (failed.GetProperty("state").GetString(), failed.GetProperty("error").GetString()));
            var only = (await EventsAsync(client, turnId)).Single().GetProperty("chunk");
            Assert.Equal(("error", "Session not found"), (only.GetProperty("type").GetString(), only.GetProperty("errorText").GetString()));
        }
        finally
        {
            host.Tools.BeforeReply = null;
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task A_queue_message_that_names_the_wrong_user_is_not_answered()
    {
        var (alice, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(alice);
        var bus = host.Services.GetRequiredService<IMessageBus>();

        // A turn that is stored but for which no message was published — and a message for it that names somebody else.
        string turnId, aliceId;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
            aliceId = db.Sessions.Single(s => s.Id == session).UserId;
            var turn = new ChatTurn
            {
                UserId = aliceId, SessionId = session,
                Request = JsonSerializer.Serialize(new { id = session, messages = new[] { UserMessage("u1", "hello") }, mode = "BUILD", model = Model }),
            };
            db.ChatTurns.Add(turn);
            db.SaveChanges();
            turnId = turn.Id;
        }

        await bus.PublishAsync(WorkQueues.ChatTurns, new WorkMessage(turnId, "somebody-else"));
        await Task.Delay(300);
        Assert.Equal("Queued", (await alice.GetFromJsonAsync<JsonElement>($"/chat/turns/{turnId}")).GetProperty("state").GetString());
        Assert.Empty(await StoredAsync(alice, session));

        // The message that names its owner is answered — and a second copy of it changes nothing.
        await bus.PublishAsync(WorkQueues.ChatTurns, new WorkMessage(turnId, aliceId));
        await bus.PublishAsync(WorkQueues.ChatTurns, new WorkMessage(turnId, aliceId));
        Assert.Equal("Completed", (await FinishedAsync(alice, turnId)).GetProperty("state").GetString());
        await Task.Delay(150);
        Assert.Equal(2, (await StoredAsync(alice, session)).Count);
    }

    [Fact]
    public async Task The_direct_stream_still_answers_and_both_ways_write_the_same_conversation()
    {
        var (client, _) = await host.NewUserAsync();
        var session = await NewSessionAsync(client);

        var direct = await client.PostAsJsonAsync("/chat", new { id = session, messages = new[] { UserMessage("u1", "hello") }, mode = "BUILD", model = Model });
        Assert.Equal("text/event-stream", direct.Content.Headers.ContentType?.MediaType);
        await direct.Content.ReadAsStringAsync();
        var turnId = (await SubmitAsync(client, session, UserMessage("u2", "again"))).GetProperty("turnId").GetString()!;
        await FinishedAsync(client, turnId);

        Assert.Equal(["user", "assistant", "user", "assistant"], (await StoredAsync(client, session)).Select(m => m.GetProperty("role").GetString()!).ToArray());
    }

    [Fact]
    public async Task Readiness_says_what_carries_the_work_and_that_turns_are_being_answered()
    {
        await Task.Delay(Cerberus.Api.Health.ReadinessService.KeepFor + TimeSpan.FromMilliseconds(200));
        var report = await Json(await host.CreateClient().GetAsync("/health/ready"));
        var checks = report.GetProperty("checks").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c);

        Assert.Equal(("healthy", "this process"), (checks["queue"].GetProperty("status").GetString(), checks["queue"].GetProperty("detail").GetString()));
        Assert.Equal(("healthy", "running"), (checks["chat-worker"].GetProperty("status").GetString(), checks["chat-worker"].GetProperty("detail").GetString()));
    }
}

[Collection("api")]
public class ChatTurnFailureTests(ApiHost host)
{
    [Fact]
    public async Task A_reply_the_model_could_not_finish_fails_the_turn_and_carries_the_reason()
    {
        var (client, _) = await host.NewUserAsync();
        var session = (await (await client.PostAsJsonAsync("/sessions", new { title = "chat" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        // More failures than the provider is retried for: the reply cannot be completed.
        host.Tools.FailNext = 50;
        try
        {
            var accepted = await client.PostAsJsonAsync("/chat/turns", new
            {
                id = session, messages = new[] { new { id = "u1", role = "user", parts = new[] { new { type = "text", text = "hello" } } } }, mode = "BUILD", model = "llama3.1",
            });
            var turnId = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("turnId").GetString()!;

            JsonElement turn;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            do
            {
                await Task.Delay(25);
                turn = await client.GetFromJsonAsync<JsonElement>($"/chat/turns/{turnId}");
            } while (turn.GetProperty("state").GetString() is "Queued" or "Running" && DateTime.UtcNow < deadline);

            Assert.Equal(("Failed", "The model could not complete the reply.", false),
                (turn.GetProperty("state").GetString(), turn.GetProperty("error").GetString(), turn.GetProperty("replyStored").GetBoolean()));
            var chunks = (await client.GetFromJsonAsync<JsonElement>($"/chat/turns/{turnId}/events")).GetProperty("events").EnumerateArray().Select(e => e.GetProperty("chunk")).ToList();
            Assert.Equal("error", chunks[^1].GetProperty("type").GetString());
        }
        finally
        {
            host.Tools.FailNext = 0;
        }
    }
}
