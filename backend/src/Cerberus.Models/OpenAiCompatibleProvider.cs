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
