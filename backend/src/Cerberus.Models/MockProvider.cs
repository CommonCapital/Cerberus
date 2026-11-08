using System.Runtime.CompilerServices;
using Cerberus.Core.Models;

namespace Cerberus.Models;

/// <summary>
/// Dev/test provider: streams a deterministic reply token-by-token with no
/// network call or API key. Used to exercise the streaming pipeline and the
/// AI-SDK UI-message stream wire format headlessly.
/// </summary>
public sealed class MockProvider : IModelProvider
{
    public ModelProvider Provider => ModelProvider.Mock;

    public async IAsyncEnumerable<string> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lastUser = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var reply = $"Echo: {lastUser}".Trim();

        foreach (var word in reply.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return word + " ";
            await Task.Delay(15, cancellationToken);
        }
    }
}
