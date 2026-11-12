namespace Cerberus.Core.Models;

public enum ModelProvider
{
    Anthropic,
    OpenAI,
    Ollama,
    Mock,
}

public sealed record ChatModelInfo(string Id, ModelProvider Provider);

/// <summary>
/// Supported chat models. Mirrors the TypeScript shared registry. Ollama models
/// run locally and are free; cloud models require a user-supplied API key.
/// </summary>
public static class ModelRegistry
{
    public static readonly IReadOnlyList<ChatModelInfo> Models =
    [
        new("claude-sonnet-4-6", ModelProvider.Anthropic),
        new("claude-haiku-4-5", ModelProvider.Anthropic),
        new("claude-opus-4-6", ModelProvider.Anthropic),
        new("gpt-5.4", ModelProvider.OpenAI),
        new("gpt-5.4-mini", ModelProvider.OpenAI),
        new("gpt-5.4-nano", ModelProvider.OpenAI),
        new("llama3.1", ModelProvider.Ollama),
        new("qwen2.5-coder", ModelProvider.Ollama),
        new("deepseek-r1", ModelProvider.Ollama),
        new("codestral", ModelProvider.Ollama),
        // Dev/test model: streams a canned reply with no network or API key.
        new("echo", ModelProvider.Mock),
    ];

    public const string DefaultModelId = "claude-opus-4-6";

    public static ChatModelInfo? Find(string modelId) =>
        Models.FirstOrDefault(m => m.Id == modelId);

    public static bool IsSupported(string modelId) => Find(modelId) is not null;

    /// <summary>
    /// A conservative size of the context window, in tokens, by provider. Local
    /// models are counted small: Ollama runs a model with a short window unless
    /// it is configured otherwise, and silently drops what does not fit.
    /// </summary>
    public static int ContextWindowOf(string modelId) => Find(modelId)?.Provider switch
    {
        ModelProvider.Anthropic => 200_000,
        ModelProvider.OpenAI => 128_000,
        _ => 8_000,
    };
}
