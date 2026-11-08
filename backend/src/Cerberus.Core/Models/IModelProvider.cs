namespace Cerberus.Core.Models;

public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// Per-request credentials. Keys are supplied by the user (request or env),
/// never baked in. Ollama needs no key.
/// </summary>
public sealed record ModelCredentials(
    string? AnthropicApiKey = null,
    string? OpenAiApiKey = null,
    string? OllamaBaseUrl = null);

public sealed record ChatCompletionRequest(
    string ModelId,
    IReadOnlyList<ChatMessage> Messages,
    string? System,
    ModelCredentials Credentials);

/// <summary>
/// A provider that streams a model completion as incremental text deltas.
/// </summary>
public interface IModelProvider
{
    ModelProvider Provider { get; }

    IAsyncEnumerable<string> StreamAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves the provider responsible for a given model id.</summary>
public interface IModelProviderResolver
{
    IModelProvider Resolve(string modelId);
}
