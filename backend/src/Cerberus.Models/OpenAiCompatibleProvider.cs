using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Cerberus.Core.Models;

namespace Cerberus.Models;

/// <summary>
/// Streams completions from any OpenAI-compatible Chat Completions endpoint
/// (used for both OpenAI and local Ollama).
/// </summary>
public abstract class OpenAiCompatibleProvider(HttpClient httpClient) : IModelProvider
{
    public abstract ModelProvider Provider { get; }

    protected abstract (string BaseUrl, string ApiKey) ResolveEndpoint(ModelCredentials credentials);

    public async IAsyncEnumerable<string> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (baseUrl, apiKey) = ResolveEndpoint(request.Credentials);

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            messages.Add(new { role = "system", content = request.System });
        }
        foreach (var m in request.Messages)
        {
            messages.Add(new { role = m.Role, content = m.Content });
        }

        var payload = JsonSerializer.Serialize(new
        {
            model = request.ModelId,
            messages,
            stream = true,
        });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            var delta = ExtractDelta(data);
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

    /// <summary>
    /// One step of a conversation with tools, over Chat Completions. Tool calls
    /// arrive as fragments keyed by index; each is reported as it starts, as its
    /// arguments grow, and once complete when the step ends.
    /// </summary>
    public async IAsyncEnumerable<ChatEvent> StreamChatAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (baseUrl, apiKey) = ResolveEndpoint(request.Credentials);

        var body = new Dictionary<string, object?>
        {
            ["model"] = request.ModelId,
            ["messages"] = ToWireMessages(request),
            ["stream"] = true,
            ["stream_options"] = new { include_usage = true },
        };
        if (request.Tools.Count > 0)
        {
            body["tools"] = request.Tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.InputSchema },
            }).ToList();
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"The model provider answered {(int)response.StatusCode}: {Truncate(detail, 500)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Arguments)>();
        string? finishReason = null;
        var reasoningOpen = false;

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null) break;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch (JsonException) { continue; }   // a malformed chunk is skipped, not fatal

            using (doc)
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    yield return new UsageReport(IntOf(usage, "prompt_tokens"), IntOf(usage, "completion_tokens"));
                }
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];

                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    // Some OpenAI-compatible servers stream a model's reasoning in a separate field.
                    var reasoning = StringOf(delta, "reasoning_content") ?? StringOf(delta, "reasoning");
                    if (!string.IsNullOrEmpty(reasoning))
                    {
                        reasoningOpen = true;
                        yield return new ReasoningDelta(reasoning);
                    }

                    var content = StringOf(delta, "content");
                    if (!string.IsNullOrEmpty(content))
                    {
                        if (reasoningOpen) { reasoningOpen = false; yield return new ReasoningEnd(); }
                        yield return new TextDelta(content);
                    }

                    if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        if (reasoningOpen) { reasoningOpen = false; yield return new ReasoningEnd(); }
                        foreach (var toolCall in toolCalls.EnumerateArray())
                        {
                            var index = toolCall.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : calls.Count;
                            var function = toolCall.TryGetProperty("function", out var f) ? f : default;
                            var name = function.ValueKind == JsonValueKind.Object ? StringOf(function, "name") : null;
                            var arguments = function.ValueKind == JsonValueKind.Object ? StringOf(function, "arguments") : null;

                            if (!calls.TryGetValue(index, out var call))
                            {
                                var id = StringOf(toolCall, "id") is { Length: > 0 } given ? given : $"call_{Guid.NewGuid():n}";
                                call = (id, name ?? string.Empty, new StringBuilder());
                                calls[index] = call;
                                yield return new ToolCallStart(call.Id, call.Name);
                            }
                            if (!string.IsNullOrEmpty(arguments))
                            {
                                call.Arguments.Append(arguments);
                                yield return new ToolCallInputDelta(call.Id, arguments);
                            }
                        }
                    }
                }

                if (StringOf(choice, "finish_reason") is { Length: > 0 } reason) finishReason = reason;
            }
        }

        if (reasoningOpen) yield return new ReasoningEnd();
        foreach (var call in calls.Values)
        {
            yield return new ToolCallComplete(call.Id, call.Name, call.Arguments.ToString());
        }
        yield return new StepFinished(finishReason);
    }

    /// <summary>The conversation in Chat Completions form: tool calls on the assistant message, one "tool" message per result.</summary>
    public static List<object> ToWireMessages(ChatRequest request)
    {
        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            messages.Add(new { role = "system", content = request.System });
        }

        foreach (var turn in request.Turns)
        {
            switch (turn.Role)
            {
                case "user":
                    messages.Add(new { role = "user", content = ChatEvents.TextOf(turn) });
                    break;

                case "assistant":
                    var text = ChatEvents.TextOf(turn);
                    var toolCalls = turn.Parts.OfType<ToolCallPart>().Select(c => new
                    {
                        id = c.Id,
                        type = "function",
                        function = new { name = c.Name, arguments = c.Input.GetRawText() },
                    }).ToList();
                    if (toolCalls.Count > 0)
                    {
                        messages.Add(new { role = "assistant", content = text.Length > 0 ? text : null, tool_calls = toolCalls });
                    }
                    else if (text.Length > 0)
                    {
                        messages.Add(new { role = "assistant", content = text });
                    }
                    break;

                case "tool":
                    foreach (var result in turn.Parts.OfType<ToolResultPart>())
                    {
                        messages.Add(new
                        {
                            role = "tool",
                            tool_call_id = result.ToolCallId,
                            content = result.IsError ? $"Error: {result.Content}" : result.Content,
                        });
                    }
                    break;
            }
        }
        return messages;
    }

    private static string? StringOf(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? IntOf(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) ? n : null;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private static string? ExtractDelta(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return null;
            }
            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }
        catch (JsonException)
        {
            // Ignore malformed chunks.
        }
        return null;
    }
}

public sealed class OpenAiProvider(HttpClient httpClient) : OpenAiCompatibleProvider(httpClient)
{
    public override ModelProvider Provider => ModelProvider.OpenAI;

    protected override (string BaseUrl, string ApiKey) ResolveEndpoint(ModelCredentials credentials)
    {
        var apiKey = credentials.OpenAiApiKey ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "No OpenAI API key. Set OPENAI_API_KEY or use a local Ollama model.");
        }
        return ("https://api.openai.com/v1", apiKey);
    }
}

public sealed class OllamaProvider(HttpClient httpClient) : OpenAiCompatibleProvider(httpClient)
{
    public override ModelProvider Provider => ModelProvider.Ollama;

    protected override (string BaseUrl, string ApiKey) ResolveEndpoint(ModelCredentials credentials)
    {
        var baseUrl = credentials.OllamaBaseUrl
            ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
            ?? "http://localhost:11434/v1";
        return (baseUrl, "ollama");
    }
}
