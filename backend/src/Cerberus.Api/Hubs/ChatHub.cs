using System.Runtime.CompilerServices;
using Cerberus.Api.Auth;
using Cerberus.Api.Settings;
using Cerberus.Core.Models;
using Microsoft.AspNetCore.SignalR;

namespace Cerberus.Api.Hubs;

/// <summary>
/// Chat text streaming over SignalR (<c>/hub/chat</c>): streams a model's text
/// deltas to the caller. A plain-text alternative to the HTTP <c>/chat</c>
/// endpoint, which is what the CLI uses and which carries tool calls.
/// A client connects with its bearer token, exactly as for <c>/hub/jobs</c>;
/// a connection without a valid session is dropped and can call nothing.
/// The model runs with the caller's own stored keys.
/// </summary>
public sealed class ChatHub(IModelProviderResolver resolver) : Hub
{
    private const string UserIdItem = "userId";

    /// <summary>Upper bounds on what one call may send to a model.</summary>
    public const int MaxMessages = 200;
    public const int MaxCharacters = 400_000;

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var token = http?.Request.Query["access_token"].FirstOrDefault()
            ?? RequireAuthFilter.ExtractBearerToken(http?.Request.Headers.Authorization);

        string? userId = null;
        if (!string.IsNullOrWhiteSpace(token) && http is not null)
        {
            var auth = http.RequestServices.GetRequiredService<AuthService>();
            userId = await auth.ValidateTokenAsync(token, Context.ConnectionAborted);
        }
        if (userId is null)
        {
            Context.Abort();   // no valid session: nothing can be invoked on this connection
            return;
        }

        Context.Items[UserIdItem] = userId;
        await base.OnConnectedAsync();
    }

    public async IAsyncEnumerable<string> Stream(
        HubChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Checked again here: a method call must never run for a connection that did not authenticate.
        if (Context.Items[UserIdItem] is not string userId) throw new HubException("Not signed in.");

        if (request?.Messages is not { Count: > 0 } || request.Messages.Count > MaxMessages ||
            request.Messages.Any(m => m is null || m.Role is not ("user" or "assistant") || m.Content is null) ||
            request.Messages.Sum(m => m.Content.Length) + (request.System?.Length ?? 0) > MaxCharacters)
        {
            throw new HubException("messages must be a non-empty list of user and assistant messages of reasonable size.");
        }
        if (string.IsNullOrWhiteSpace(request.Model) || !ModelRegistry.IsSupported(request.Model))
        {
            throw new HubException("model must be a listed model or provider:model (anthropic:, openai:, deepseek: or ollama:).");
        }

        var http = Context.GetHttpContext() ?? throw new HubException("The connection is gone.");
        var credentials = await http.RequestServices.GetRequiredService<ApiKeyService>()
            .ResolveCredentialsAsync(userId, cancellationToken);

        var provider = resolver.Resolve(request.Model);
        var messages = request.Messages.Select(m => new ChatMessage(m.Role, m.Content)).ToList();
        var completion = new ChatCompletionRequest(request.Model, messages, request.System, credentials);

        await foreach (var delta in provider.StreamAsync(completion, cancellationToken))
        {
            yield return delta;
        }
    }
}
