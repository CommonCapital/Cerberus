namespace Cerberus.Core.Models;

public enum ModelProvider
{
    Anthropic,
    OpenAI,
    Ollama,
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
    ];

    public const string DefaultModelId = "claude-opus-4-6";

    public static ChatModelInfo? Find(string modelId) =>
        Models.FirstOrDefault(m => m.Id == modelId);

    public static bool IsSupported(string modelId) => Find(modelId) is not null;
}
