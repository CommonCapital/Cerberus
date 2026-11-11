using System.Net;
using System.Text;
using System.Text.Json;
using Cerberus.Core.Models;
using Cerberus.Models;

namespace Cerberus.Tests;

/// <summary>
/// The Anthropic provider against a canned event stream — no network and no
/// API key. The stream follows the Messages API's documented event shapes; it
/// has not been captured from the live API.
/// </summary>
public class AnthropicProviderTests
{
    private sealed class CannedHandler(string sse, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(status) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
        }
    }

    private static string Sse(params (string Event, string Data)[] events) =>
        string.Concat(events.Select(e => $"event: {e.Event}\ndata: {e.Data}\n\n"));

    private static readonly JsonElement Schema = JsonDocument.Parse(
        """{"type":"object","properties":{"path":{"type":"string","description":"Relative path"}},"required":["path"],"additionalProperties":false}""").RootElement.Clone();

    private static ChatRequest Request(string model, params ChatTurn[] turns) =>
        new(model, "You are terse.", turns, [new ToolDefinition("readFile", "Read a file.", Schema)],
            new ModelCredentials(AnthropicApiKey: "sk-ant-api03-not-a-real-key"));

    private const string MessageStart =
        """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-6","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":25,"output_tokens":1}}}""";

    private static readonly string ToolUseStream = Sse(
        ("message_start", MessageStart),
        ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Need the file."}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-abc"}}"""),
        ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
        ("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Reading "}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"it."}}"""),
        ("content_block_stop", """{"type":"content_block_stop","index":1}"""),
        ("content_block_start", """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_1","name":"readFile","input":{}}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}"""),
        ("content_block_delta", """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"a.txt\"}"}}"""),
        ("content_block_stop", """{"type":"content_block_stop","index":2}"""),
        ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":40}}"""),
        ("message_stop", """{"type":"message_stop"}"""));

    private static async Task<(List<ChatEvent> Events, CannedHandler Handler)> Run(string sse, ChatRequest request)
    {
        var handler = new CannedHandler(sse);
        var events = new List<ChatEvent>();
        await foreach (var e in new AnthropicProvider(new HttpClient(handler)).StreamChatAsync(request)) events.Add(e);
        return (events, handler);
    }

    [Fact]
    public async Task Thinking_text_and_a_tool_call_are_read_from_the_stream()
    {
        var (events, _) = await Run(ToolUseStream, Request("claude-sonnet-4-6", ChatTurn.User("read a.txt")));

        Assert.Equal("Need the file.", string.Concat(events.OfType<ReasoningDelta>().Select(r => r.Text)));
        Assert.Equal(new ReasoningEnd(Signature: "sig-abc"), events.OfType<ReasoningEnd>().Single());
        Assert.Equal("Reading it.", string.Concat(events.OfType<TextDelta>().Select(t => t.Text)));
        Assert.Equal(new ToolCallStart("toolu_1", "readFile"), events.OfType<ToolCallStart>().Single());
        Assert.Equal(new ToolCallComplete("toolu_1", "readFile", """{"path":"a.txt"}"""), events.OfType<ToolCallComplete>().Single());
        Assert.Equal([new UsageReport(25, null), new UsageReport(null, 40)], events.OfType<UsageReport>().ToArray());
        Assert.Equal(new StepFinished("tool_use"), events[^1]);

        // Order matters to the UI: reasoning, then text, then the call.
        var kinds = events.Select(e => e.GetType().Name).Where(n => n is "ReasoningEnd" or "TextDelta" or "ToolCallStart").Distinct().ToArray();
        Assert.Equal(["ReasoningEnd", "TextDelta", "ToolCallStart"], kinds);
    }

    [Fact]
    public async Task The_request_goes_to_the_messages_api_with_the_key_tools_and_adaptive_thinking()
    {
        var (_, handler) = await Run(ToolUseStream, Request("claude-sonnet-4-6", ChatTurn.User("read a.txt")));

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/v1/messages", request.RequestUri!.AbsolutePath);
        // Current SDK releases send an API key as a bearer credential.
        Assert.Equal("Bearer sk-ant-api03-not-a-real-key", request.Headers.Authorization?.ToString());
        Assert.True(request.Headers.Contains("anthropic-version"));

        var sent = JsonDocument.Parse(body).RootElement;
        Assert.Equal("claude-sonnet-4-6", sent.GetProperty("model").GetString());
        Assert.True(sent.GetProperty("stream").GetBoolean());
        Assert.Equal(64000, sent.GetProperty("max_tokens").GetInt32());
        Assert.Contains("terse", sent.GetProperty("system").ToString());
        Assert.Equal("adaptive", sent.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("summarized", sent.GetProperty("thinking").GetProperty("display").GetString());

        var tool = sent.GetProperty("tools")[0];
        Assert.Equal("readFile", tool.GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
        Assert.Equal("string", tool.GetProperty("input_schema").GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
        Assert.Equal("path", tool.GetProperty("input_schema").GetProperty("required")[0].GetString());
        Assert.True(tool.GetProperty("eager_input_streaming").GetBoolean());
    }

    [Fact]
    public async Task A_continued_conversation_sends_thinking_back_unchanged_with_the_tool_result()
    {
        var input = JsonDocument.Parse("""{"path":"a.txt"}""").RootElement.Clone();
        var (_, handler) = await Run(ToolUseStream, Request("claude-sonnet-4-6",
            ChatTurn.User("read a.txt"),
            new ChatTurn("assistant", [new ReasoningPart("Need the file.", "sig-abc"), new TextPart("Reading it."), new ToolCallPart("toolu_1", "readFile", input)]),
            new ChatTurn("tool", [new ToolResultPart("toolu_1", "readFile", "AAA"), new ToolResultPart("toolu_2", "readFile", "missing", IsError: true)])));

        var messages = JsonDocument.Parse(handler.Calls[0].Body).RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m.GetProperty("role").GetString()!).ToArray());

        var assistant = messages[1].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(["thinking", "text", "tool_use"], assistant.Select(b => b.GetProperty("type").GetString()!).ToArray());
        Assert.Equal(("Need the file.", "sig-abc"), (assistant[0].GetProperty("thinking").GetString(), assistant[0].GetProperty("signature").GetString()));
        Assert.Equal(("toolu_1", "a.txt"), (assistant[2].GetProperty("id").GetString(), assistant[2].GetProperty("input").GetProperty("path").GetString()));

        // Both results of the step are in ONE user message.
        var results = messages[2].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(["tool_result", "tool_result"], results.Select(b => b.GetProperty("type").GetString()!).ToArray());
        Assert.Equal("toolu_1", results[0].GetProperty("tool_use_id").GetString());
        Assert.True(results[1].GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public async Task Redacted_thinking_is_kept_so_it_can_be_sent_back()
    {
        var stream = Sse(
            ("message_start", MessageStart),
            ("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"redacted_thinking","data":"opaque-blob"}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":0}"""),
            ("content_block_start", """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}"""),
            ("content_block_delta", """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Done."}}"""),
            ("content_block_stop", """{"type":"content_block_stop","index":1}"""),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}"""),
            ("message_stop", """{"type":"message_stop"}"""));

        var (events, _) = await Run(stream, Request("claude-sonnet-4-6", ChatTurn.User("hi")));

        Assert.Equal(new ReasoningEnd(RedactedData: "opaque-blob"), events.OfType<ReasoningEnd>().Single());
        Assert.Equal(new StepFinished("end_turn"), events[^1]);

        var parameters = AnthropicProvider.BuildParameters(Request("claude-sonnet-4-6",
            ChatTurn.User("hi"), new ChatTurn("assistant", [new ReasoningPart("", RedactedData: "opaque-blob"), new TextPart("Done.")]), ChatTurn.User("more")));
        var json = JsonSerializer.Serialize(parameters.Messages[1]);
        Assert.Contains("redacted_thinking", json);
        Assert.Contains("opaque-blob", json);
    }

    [Fact]
    public async Task A_refusal_is_reported_as_a_failure_not_as_a_finished_reply()
    {
        var stream = Sse(
            ("message_start", MessageStart),
            ("message_delta", """{"type":"message_delta","delta":{"stop_reason":"refusal","stop_sequence":null},"usage":{"output_tokens":0}}"""),
            ("message_stop", """{"type":"message_stop"}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(stream, Request("claude-sonnet-4-6", ChatTurn.User("hi"))));

        Assert.Contains("declined", ex.Message);
    }

    [Theory]
    [InlineData("claude-opus-4-6", true)]
    [InlineData("claude-sonnet-4-6", true)]
    [InlineData("claude-haiku-4-5", false)]
    public async Task Thinking_is_requested_only_on_models_that_take_adaptive_thinking(string model, bool thinking)
    {
        var (_, handler) = await Run(ToolUseStream, Request(model, ChatTurn.User("hi")));

        var sent = JsonDocument.Parse(handler.Calls[0].Body).RootElement;
        Assert.Equal(thinking, sent.TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task The_text_only_interface_still_works()
    {
        var handler = new CannedHandler(ToolUseStream);
        var text = new StringBuilder();
        var request = new ChatCompletionRequest("claude-sonnet-4-6", [new ChatMessage("user", "hi")], null,
            new ModelCredentials(AnthropicApiKey: "sk-ant-api03-not-a-real-key"));

        await foreach (var delta in new AnthropicProvider(new HttpClient(handler)).StreamAsync(request)) text.Append(delta);

        Assert.Equal("Reading it.", text.ToString());
        Assert.False(JsonDocument.Parse(handler.Calls[0].Body).RootElement.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Without_a_key_no_request_is_made()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))) return;   // a key in the environment would be used
        var handler = new CannedHandler(ToolUseStream);
        var request = Request("claude-sonnet-4-6", ChatTurn.User("hi")) with { Credentials = new ModelCredentials() };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in new AnthropicProvider(new HttpClient(handler)).StreamChatAsync(request)) { }
        });

        Assert.Contains("No Anthropic API key", ex.Message);
        Assert.Empty(handler.Calls);
    }
}
