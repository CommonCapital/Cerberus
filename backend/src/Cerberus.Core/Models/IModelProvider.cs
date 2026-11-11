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

    /// <summary>
    /// Streams one step of a conversation that may use tools and reasoning.
    /// The default serves providers that only produce text: it flattens the
    /// conversation to text turns and offers no tools.
    /// </summary>
    IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var messages = request.Turns
            .Where(t => t.Role is "user" or "assistant")
            .Select(t => new ChatMessage(t.Role, ChatEvents.TextOf(t)))
            .Where(m => m.Content.Length > 0)
            .ToList();
        return ChatEvents.FromText(
            StreamAsync(new ChatCompletionRequest(request.ModelId, messages, request.System, request.Credentials), cancellationToken),
            cancellationToken);
    }
}

/// <summary>Resolves the provider responsible for a given model id.</summary>
public interface IModelProviderResolver
{
    IModelProvider Resolve(string modelId);
}
