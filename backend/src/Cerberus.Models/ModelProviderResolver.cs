using Cerberus.Core.Models;

namespace Cerberus.Models;

public sealed class ModelProviderResolver(IEnumerable<IModelProvider> providers) : IModelProviderResolver
{
    private readonly Dictionary<ModelProvider, IModelProvider> _byProvider =
        providers.ToDictionary(p => p.Provider);

    public IModelProvider Resolve(string modelId)
    {
        var info = ModelRegistry.Find(modelId)
            ?? throw new InvalidOperationException($"Unsupported model: {modelId}");

        if (!_byProvider.TryGetValue(info.Provider, out var provider))
        {
            throw new InvalidOperationException($"No provider registered for {info.Provider}");
        }

        return provider;
    }
}
