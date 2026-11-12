using System.Text.RegularExpressions;

namespace Cerberus.Core.Models;

public enum ModelProvider
{
    Anthropic,
    OpenAI,
    DeepSeek,
    Ollama,
    Mock,
}

/// <param name="Id">The id a client sends: a listed model, a model of a known family, or <c>provider:model</c>.</param>
/// <param name="ProviderModel">The model as its provider knows it, when that differs from the id (the part after the prefix).</param>
public sealed record ChatModelInfo(string Id, ModelProvider Provider, string? ProviderModel = null)
{
    /// <summary>The model name sent to the provider.</summary>
    public string Name => ProviderModel ?? Id;
}

/// <summary>
/// Which models can be used, and which provider serves each. Any model of a
/// supported provider can be chosen — the list below is what is offered, not
/// a limit:
/// <list type="bullet">
/// <item>a listed id (<c>claude-opus-5-5</c>, <c>llama3.1</c>);</item>
/// <item>a provider and a model, <c>provider:model</c> — <c>anthropic:</c>,
/// <c>openai:</c>, <c>deepseek:</c>, <c>ollama:</c> — for any model that
/// provider has (<c>ollama:qwen2.5-coder:7b</c>, <c>openai:gpt-4.1</c>);</item>
/// <item>an id of a family that belongs to one provider without doubt
/// (<c>claude-…</c>, <c>gpt-…</c>, <c>deepseek-chat</c>).</item>
/// </list>
/// A bare name that is none of these is not guessed at: it could as well be a
/// typing mistake as a local model, and the two differ in where code is sent.
/// Ollama models run locally and are free; the others need the user's API key.
/// </summary>
public static partial class ModelRegistry
{
    public static readonly IReadOnlyList<ChatModelInfo> Models =
    [
        new("claude-opus-5-5", ModelProvider.Anthropic),
        new("claude-sonnet-5-5", ModelProvider.Anthropic),
        new("claude-haiku-5-5", ModelProvider.Anthropic),
        new("claude-fable-5-2", ModelProvider.Anthropic),
        new("gpt-5.4", ModelProvider.OpenAI),
        new("gpt-5.4-mini", ModelProvider.OpenAI),
        new("gpt-5.4-nano", ModelProvider.OpenAI),
        new("deepseek-chat", ModelProvider.DeepSeek),
        new("deepseek-reasoner", ModelProvider.DeepSeek),
        new("llama3.1", ModelProvider.Ollama),
        new("qwen2.5-coder", ModelProvider.Ollama),
        new("deepseek-r1", ModelProvider.Ollama),
        new("codestral", ModelProvider.Ollama),
        // Dev/test model: streams a canned reply with no network or API key.
        new("echo", ModelProvider.Mock),
    ];

    public const string DefaultModelId = "claude-opus-5-5";

    /// <summary>The prefix that names each provider in <c>provider:model</c>.</summary>
    public static readonly IReadOnlyDictionary<string, ModelProvider> Prefixes = new Dictionary<string, ModelProvider>(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = ModelProvider.Anthropic,
        ["openai"] = ModelProvider.OpenAI,
        ["deepseek"] = ModelProvider.DeepSeek,
        ["ollama"] = ModelProvider.Ollama,
    };

    public const int MaxIdLength = 200;

    public static ChatModelInfo? Find(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId) || modelId.Length > MaxIdLength || !WellFormed().IsMatch(modelId)) return null;
        if (Models.FirstOrDefault(m => m.Id == modelId) is { } listed) return listed;

        // provider:model — the rest, colons included, is the provider's own name for the model.
        var colon = modelId.IndexOf(':');
        if (colon > 0 && Prefixes.TryGetValue(modelId[..colon], out var named))
        {
            var name = modelId[(colon + 1)..];
            return name.Length > 0 ? new ChatModelInfo(modelId, named, name) : null;
        }

        // Families that belong to one provider.
        if (modelId.StartsWith("claude-", StringComparison.Ordinal)) return new ChatModelInfo(modelId, ModelProvider.Anthropic);
        if (modelId.StartsWith("gpt-", StringComparison.Ordinal) || modelId.StartsWith("chatgpt-", StringComparison.Ordinal) || OpenAiReasoning().IsMatch(modelId))
        {
            return new ChatModelInfo(modelId, ModelProvider.OpenAI);
        }
        return null;
    }

    public static bool IsSupported(string? modelId) => Find(modelId) is not null;

    /// <summary>The model as its provider knows it: the id without a provider prefix.</summary>
    public static string NameOf(string modelId) => Find(modelId)?.Name ?? modelId;

    /// <summary>How to say what can be given as a model, for an error message.</summary>
    public const string Accepted =
        "a listed model, or any model of a provider as provider:model (anthropic:, openai:, deepseek:, ollama:), for example ollama:qwen2.5-coder:7b";

    /// <summary>
    /// A conservative size of the context window, in tokens, by provider. Local
    /// models are counted small: Ollama runs a model with a short window unless
    /// it is configured otherwise, and silently drops what does not fit.
    /// </summary>
    public static int ContextWindowOf(string modelId) => Find(modelId)?.Provider switch
    {
        ModelProvider.Anthropic => 200_000,
        ModelProvider.OpenAI => 128_000,
        ModelProvider.DeepSeek => 64_000,
        _ => 8_000,
    };

    /// <summary>Names a provider could have for a model: letters, digits and the punctuation model names use. Nothing that could be anything else.</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._:/@+\-]*\z")]
    private static partial Regex WellFormed();

    [GeneratedRegex(@"^o\d")]
    private static partial Regex OpenAiReasoning();
}
