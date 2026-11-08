using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Cerberus.Core.Models;

namespace Cerberus.Models;

/// <summary>Streams completions from the Anthropic Messages API.</summary>
public sealed class AnthropicProvider(HttpClient httpClient) : IModelProvider
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    public ModelProvider Provider => ModelProvider.Anthropic;

    public async IAsyncEnumerable<string> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var apiKey = request.Credentials.AnthropicApiKey
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "No Anthropic API key. Set ANTHROPIC_API_KEY or use a local Ollama model.");
        }

        var payload = JsonSerializer.Serialize(new
        {
            model = request.ModelId,
            max_tokens = 4096,
            system = request.System,
            stream = true,
            messages = request.Messages.Select(m => new { role = m.Role, content = m.Content }),
        });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        httpRequest.Headers.Add("x-api-key", apiKey);
        httpRequest.Headers.Add("anthropic-version", AnthropicVersion);

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
            var delta = ExtractTextDelta(data);
            if (!string.IsNullOrEmpty(delta)) yield return delta;
        }
    }

    private static string? ExtractTextDelta(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) &&
                type.GetString() == "content_block_delta" &&
                root.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }
        catch (JsonException)
        {
            // Ignore malformed chunks.
        }
        return null;
    }
}
