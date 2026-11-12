using System.Text.Json;

namespace Cerberus.Api.Chat;

/// <summary>
/// Writes the AI-SDK "UI message stream" wire format so that @ai-sdk/react's
/// DefaultChatTransport can consume this .NET endpoint unchanged.
///
/// Framing (per the `ai` package): each chunk is serialized as
/// `data: {json}\n\n`, and the stream ends with `data: [DONE]\n\n`. Required
/// response headers are applied by <see cref="ApplyHeaders"/>.
/// </summary>
public sealed class UiMessageStreamWriter
{
    private readonly Func<string, CancellationToken, Task> _chunk;
    private readonly Func<CancellationToken, Task> _done;

    /// <summary>Writes the stream as the body of an HTTP response.</summary>
    public UiMessageStreamWriter(HttpResponse response)
    {
        _chunk = async (json, ct) =>
        {
            await response.WriteAsync($"data: {json}\n\n", ct);
            await response.Body.FlushAsync(ct);
        };
        _done = async ct =>
        {
            await response.WriteAsync("data: [DONE]\n\n", ct);
            await response.Body.FlushAsync(ct);
        };
    }

    /// <summary>
    /// Hands each chunk — the same JSON an HTTP client would get — to something else: the delivery of a
    /// queued turn, which numbers it, pushes it and stores it. The end of the stream is the turn's own end.
    /// </summary>
    public UiMessageStreamWriter(Func<string, CancellationToken, Task> chunk)
    {
        _chunk = chunk;
        _done = _ => Task.CompletedTask;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static void ApplyHeaders(HttpResponse response)
    {
        response.Headers["Content-Type"] = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
        response.Headers["x-vercel-ai-ui-message-stream"] = "v1";
        response.Headers["x-accel-buffering"] = "no";
    }

    public Task StartAsync(string messageId, object? messageMetadata, CancellationToken ct) =>
        WriteChunkAsync(new { type = "start", messageId, messageMetadata }, ct);

    public Task StartStepAsync(CancellationToken ct) =>
        WriteChunkAsync(new { type = "start-step" }, ct);

    public Task TextStartAsync(string id, CancellationToken ct) =>
        WriteChunkAsync(new { type = "text-start", id }, ct);

    public Task TextDeltaAsync(string id, string delta, CancellationToken ct) =>
        WriteChunkAsync(new { type = "text-delta", id, delta }, ct);

    public Task TextEndAsync(string id, CancellationToken ct) =>
        WriteChunkAsync(new { type = "text-end", id }, ct);

    public Task ReasoningStartAsync(string id, CancellationToken ct) =>
        WriteChunkAsync(new { type = "reasoning-start", id }, ct);

    public Task ReasoningDeltaAsync(string id, string delta, CancellationToken ct) =>
        WriteChunkAsync(new { type = "reasoning-delta", id, delta }, ct);

    public Task ReasoningEndAsync(string id, object? providerMetadata, CancellationToken ct) =>
        WriteChunkAsync(new { type = "reasoning-end", id, providerMetadata }, ct);

    /// <param name="providerExecuted">True for tools this server runs itself; the client then neither runs nor waits for them.</param>
    public Task ToolInputStartAsync(string toolCallId, string toolName, bool providerExecuted, CancellationToken ct) =>
        WriteChunkAsync(new { type = "tool-input-start", toolCallId, toolName, providerExecuted = providerExecuted ? true : (bool?)null }, ct);

    public Task ToolInputDeltaAsync(string toolCallId, string inputTextDelta, CancellationToken ct) =>
        WriteChunkAsync(new { type = "tool-input-delta", toolCallId, inputTextDelta }, ct);

    public Task ToolInputAvailableAsync(string toolCallId, string toolName, JsonElement input, bool providerExecuted, CancellationToken ct) =>
        WriteChunkAsync(new { type = "tool-input-available", toolCallId, toolName, input, providerExecuted = providerExecuted ? true : (bool?)null }, ct);

    public Task ToolInputErrorAsync(string toolCallId, string toolName, string input, string errorText, CancellationToken ct) =>
        WriteChunkAsync(new { type = "tool-input-error", toolCallId, toolName, input, errorText }, ct);

    public Task ToolOutputAvailableAsync(string toolCallId, JsonElement output, CancellationToken ct) =>
        WriteChunkAsync(new { type = "tool-output-available", toolCallId, output, providerExecuted = true }, ct);

    public Task ToolOutputErrorAsync(string toolCallId, string errorText, CancellationToken ct) =>
        WriteChunkAsync(new { type = "tool-output-error", toolCallId, errorText, providerExecuted = true }, ct);

    public Task FinishStepAsync(CancellationToken ct) =>
        WriteChunkAsync(new { type = "finish-step" }, ct);

    public Task FinishAsync(object? messageMetadata, CancellationToken ct) =>
        WriteChunkAsync(new { type = "finish", messageMetadata }, ct);

    /// <summary>
    /// A transient notice for the client (AI-SDK "data" part): shown while it matters, never stored in the message.
    /// </summary>
    public Task NoticeAsync(string kind, object data, CancellationToken ct) =>
        WriteChunkAsync(new { type = $"data-{kind}", data, transient = true }, ct);

    public Task ErrorAsync(string errorText, CancellationToken ct) =>
        WriteChunkAsync(new { type = "error", errorText }, ct);

    public Task DoneAsync(CancellationToken ct) => _done(ct);

    private Task WriteChunkAsync(object chunk, CancellationToken ct) => _chunk(JsonSerializer.Serialize(chunk, JsonOptions), ct);
}
