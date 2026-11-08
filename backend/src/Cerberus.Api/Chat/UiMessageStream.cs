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
public sealed class UiMessageStreamWriter(HttpResponse response)
{
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

    public Task FinishStepAsync(CancellationToken ct) =>
        WriteChunkAsync(new { type = "finish-step" }, ct);

    public Task FinishAsync(object? messageMetadata, CancellationToken ct) =>
        WriteChunkAsync(new { type = "finish", messageMetadata }, ct);

    public Task ErrorAsync(string errorText, CancellationToken ct) =>
        WriteChunkAsync(new { type = "error", errorText }, ct);

    public async Task DoneAsync(CancellationToken ct)
    {
        await response.WriteAsync("data: [DONE]\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private async Task WriteChunkAsync(object chunk, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(chunk, JsonOptions);
        await response.WriteAsync($"data: {json}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
