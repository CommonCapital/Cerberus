using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cerberus.Api.Agents;
using Cerberus.Core.Models;
using Cerberus.Models;

namespace Cerberus.Tests;

/// <summary>A provider that fails a given number of times before it answers.</summary>
public sealed class FlakyProvider(Func<int, Exception?> failure, bool failMidStream = false) : IModelProvider
{
    public ModelProvider Provider => ModelProvider.Mock;
    public int Calls { get; private set; }

    public async IAsyncEnumerable<string> StreamAsync(ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++;
        if (failure(Calls) is { } problem && !failMidStream) throw problem;
        await Task.Yield();
        yield return "text ";
        if (failure(Calls) is { } late && failMidStream) throw late;
        yield return "done";
    }

    public async IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls++;
        if (failure(Calls) is { } problem && !failMidStream) throw problem;
        await Task.Yield();
        yield return new TextDelta("Hello");
        if (failure(Calls) is { } late && failMidStream) throw late;
        yield return new StepFinished("stop");
    }
}

public class RetryTests
{
    private static readonly ChatRequest Request = new("echo", null, [ChatTurn.User("hi")], [], new ModelCredentials());

    private static (RetryPolicy Policy, List<TimeSpan> Waits) Instant(int maxRetries = 5)
    {
        var waits = new List<TimeSpan>();
        return (new RetryPolicy(maxRetries, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), (wait, _) => { waits.Add(wait); return Task.CompletedTask; }), waits);
    }

    private static HttpRequestException Status(int code) => new($"answered {code}", null, (HttpStatusCode)code);

    private static async Task<List<ChatEvent>> Collect(IModelProvider provider)
    {
        var events = new List<ChatEvent>();
        await foreach (var e in provider.StreamChatAsync(Request)) events.Add(e);
        return events;
    }

    [Fact]
    public async Task A_busy_provider_is_tried_again_with_growing_waits_and_each_retry_is_announced()
    {
        var flaky = new FlakyProvider(call => call <= 3 ? Status(call == 1 ? 429 : 529) : null);
        var (policy, waits) = Instant();

        var events = await Collect(new RetryingProvider(flaky, policy));

        Assert.Equal(4, flaky.Calls);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], waits);
        var retries = events.OfType<RetryScheduled>().ToList();
        Assert.Equal([(1, "rate limited"), (2, "provider overloaded"), (3, "provider overloaded")], retries.Select(r => (r.Attempt, r.Reason)).ToArray());
        Assert.All(retries, r => Assert.Equal(5, r.MaxAttempts));
        Assert.Equal("Hello", Assert.Single(events.OfType<TextDelta>()).Text);   // and then the reply, once
    }

    [Fact]
    public async Task After_the_last_retry_the_failure_is_the_callers()
    {
        var flaky = new FlakyProvider(_ => Status(503));
        var (policy, waits) = Instant(maxRetries: 2);

        var problem = await Assert.ThrowsAsync<HttpRequestException>(() => Collect(new RetryingProvider(flaky, policy)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, problem.StatusCode);
        Assert.Equal(3, flaky.Calls);   // the first try and two retries
        Assert.Equal(2, waits.Count);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task A_request_that_is_itself_wrong_is_not_repeated(int status)
    {
        var flaky = new FlakyProvider(_ => Status(status));
        var (policy, waits) = Instant();

        await Assert.ThrowsAsync<HttpRequestException>(() => Collect(new RetryingProvider(flaky, policy)));

        Assert.Equal(1, flaky.Calls);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task A_missing_key_or_our_own_cancellation_is_not_retried()
    {
        var (policy, waits) = Instant();
        var noKey = new FlakyProvider(_ => new InvalidOperationException("No Anthropic API key."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(new RetryingProvider(noKey, policy)));
        Assert.Equal(1, noKey.Calls);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Null(RetryingProvider.Describe(Status(503), cancelled.Token));
        Assert.Empty(waits);
    }

    [Fact]
    public async Task Once_output_has_started_a_failure_is_not_retried_because_that_would_repeat_the_output()
    {
        var flaky = new FlakyProvider(_ => Status(529), failMidStream: true);
        var (policy, _) = Instant();
        var seen = new List<ChatEvent>();

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var e in new RetryingProvider(flaky, policy).StreamChatAsync(Request)) seen.Add(e);
        });

        Assert.Equal(1, flaky.Calls);
        Assert.Equal("Hello", Assert.Single(seen.OfType<TextDelta>()).Text);
    }

    [Fact]
    public async Task What_the_provider_asks_for_is_honoured_and_too_long_a_wait_is_reported_instead()
    {
        HttpRequestException Limited(double seconds)
        {
            var ex = Status(429);
            ex.Data[RetryPolicy.RetryAfterKey] = TimeSpan.FromSeconds(seconds);
            return ex;
        }
        var (policy, waits) = Instant();

        await Collect(new RetryingProvider(new FlakyProvider(call => call == 1 ? Limited(20) : null), policy));
        Assert.Equal([TimeSpan.FromSeconds(20)], waits);   // longer than the 2 s backoff

        var problem = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Collect(new RetryingProvider(new FlakyProvider(_ => Limited(900)), policy)));
        Assert.Contains("asked to wait 900 s", problem.Message);
    }

    [Fact]
    public async Task Network_trouble_counts_as_passing_and_the_plain_text_stream_is_retried_too()
    {
        Assert.Equal("connection lost", RetryingProvider.Describe(new HttpRequestException("x", new System.Net.Sockets.SocketException()), default));
        Assert.Equal("connection failed", RetryingProvider.Describe(new HttpRequestException("no route"), default));
        Assert.Equal("request timed out", RetryingProvider.Describe(new TaskCanceledException(), default));
        Assert.Null(RetryingProvider.Describe(new ArgumentException("bad"), default));

        var flaky = new FlakyProvider(call => call == 1 ? Status(500) : null);
        var (policy, waits) = Instant();
        var text = "";
        await foreach (var delta in new RetryingProvider(flaky, policy).StreamAsync(new ChatCompletionRequest("echo", [], null, new ModelCredentials()))) text += delta;

        Assert.Equal("text done", text);
        Assert.Single(waits);
    }

    [Fact]
    public void Waits_double_up_to_the_cap()
    {
        var (policy, _) = Instant();
        Assert.Equal([2, 4, 8, 16, 30, 30], Enumerable.Range(1, 6).Select(n => (int)policy.DelayBefore(n, null).TotalSeconds).ToArray());
    }

    [Fact]
    public async Task An_agent_run_notes_each_retry_and_carries_on()
    {
        var flaky = new FlakyProvider(call => call == 1 ? Status(529) : null);
        var (policy, _) = Instant();
        var reporter = new RecordingReporter();

        var outcome = await new AgentLoop(new SingleProviderResolver(new RetryingProvider(flaky, policy)))
            .RunAsync("echo", new ModelCredentials(), "system", "task", [], 3, reporter, default);

        Assert.Equal("Hello", outcome.FinalText);
        var note = Assert.Single(reporter.Events, e => e.Type == "note");
        Assert.Contains("provider overloaded", note.Json);
        Assert.Contains("attempt 1 of 5", note.Json);
    }
}

[Collection("api")]
public class RetryApiTests(ApiHost host)
{
    [Fact]
    public async Task Every_provider_the_api_uses_is_wrapped_and_chat_tells_the_client_about_a_retry()
    {
        var resolver = (IModelProviderResolver)host.Services.GetService(typeof(IModelProviderResolver))!;
        Assert.All(new[] { "echo", "llama3.1", "claude-haiku-4-5", "gpt-5.4" }, model => Assert.IsType<RetryingProvider>(resolver.Resolve(model)));

        // The notice as the chat stream carries it: a transient data part, never part of the stored message.
        var (client, _) = await host.NewUserAsync();
        var session = (await (await client.PostAsJsonAsync("/sessions", new { title = "chat" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        host.Tools.FailNext = 1;
        var response = await client.PostAsJsonAsync("/chat", new
        {
            id = session, mode = "PLAN", model = "llama3.1",
            messages = new[] { new { id = "u1", role = "user", parts = new[] { new { type = "text", text = "hello" } } } },
        });
        var stream = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"type\":\"data-retry\"", stream);
        Assert.Contains("\"transient\":true", stream);
        Assert.Contains("\"reason\":\"provider overloaded\"", stream);
        Assert.Contains("\"delta\":\"Echo: \"", stream);     // and then the reply arrived
        Assert.Contains("\"delta\":\"hello \"", stream);
        Assert.True(stream.IndexOf("data-retry", StringComparison.Ordinal) < stream.IndexOf("\"delta\":\"Echo: \"", StringComparison.Ordinal));
        var stored = await client.GetStringAsync($"/sessions/{session}");
        Assert.DoesNotContain("data-retry", stored);
    }
}
