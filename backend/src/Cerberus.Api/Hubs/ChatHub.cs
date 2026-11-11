using System.Runtime.CompilerServices;
using Cerberus.Core.Models;
using Microsoft.AspNetCore.SignalR;

namespace Cerberus.Api.Hubs;

/// <summary>
/// Real-time chat streaming over SignalR. Skeleton for M2 — streams model text
/// deltas to the caller. Per-user auth on the hub and the full AI-SDK message
/// protocol are follow-ups; the HTTP /chat endpoint is the primary path today.
/// </summary>
public sealed class ChatHub(IModelProviderResolver resolver) : Hub
{
    public async IAsyncEnumerable<string> Stream(
        HubChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var provider = resolver.Resolve(request.Model);
        var messages = request.Messages
            .Select(m => new ChatMessage(m.Role, m.Content))
            .ToList();

        var completion = new ChatCompletionRequest(
            request.Model, messages, request.System, new ModelCredentials());

        await foreach (var delta in provider.StreamAsync(completion, cancellationToken))
        {
            yield return delta;
        }
    }
}
