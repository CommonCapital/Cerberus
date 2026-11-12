using Cerberus.Core.Models;

namespace Cerberus.Models;

public sealed class ModelProviderResolver(IEnumerable<IModelProvider> providers, RetryPolicy? retry = null) : IModelProviderResolver
{
    // Every provider is reached through the retry wrapper: a passing failure is tried again rather than ending the reply.
    private readonly Dictionary<ModelProvider, IModelProvider> _byProvider =
        providers.ToDictionary(p => p.Provider, p => (IModelProvider)new RetryingProvider(new ByProviderName(p), retry ?? RetryPolicy.Standard));

    /// <summary>
    /// Hands the provider the model under the provider's own name: an id such as "ollama:qwen2.5-coder:7b"
    /// says which provider, and only what follows the prefix means anything to that provider.
    /// </summary>
    private sealed class ByProviderName(IModelProvider inner) : IModelProvider
    {
        public ModelProvider Provider => inner.Provider;

        public IAsyncEnumerable<string> StreamAsync(ChatCompletionRequest request, CancellationToken cancellationToken = default) =>
            inner.StreamAsync(request with { ModelId = ModelRegistry.NameOf(request.ModelId) }, cancellationToken);

        public IAsyncEnumerable<ChatEvent> StreamChatAsync(ChatRequest request, CancellationToken cancellationToken = default) =>
            inner.StreamChatAsync(request with { ModelId = ModelRegistry.NameOf(request.ModelId) }, cancellationToken);
    }

    public IModelProvider Resolve(string modelId)
    {
        var info = ModelRegistry.Find(modelId)
            ?? throw new InvalidOperationException($"Unsupported model: {modelId}. Give {ModelRegistry.Accepted}.");

        if (!_byProvider.TryGetValue(info.Provider, out var provider))
        {
            throw new InvalidOperationException($"No provider registered for {info.Provider}");
        }

        return provider;
    }
}
