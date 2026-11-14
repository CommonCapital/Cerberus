using System.Runtime.CompilerServices;
using Cerberus.Api.Auth;
using Cerberus.Api.Settings;
using Cerberus.Core.Models;
using Microsoft.AspNetCore.SignalR;

namespace Cerberus.Api.Hubs;

/// <summary>
/// The conversation's hub (<c>/hub/chat</c>). A client connects with its bearer token, exactly as for
/// <c>/hub/jobs</c>; a connection without a valid session is dropped, joins nothing and can call nothing.
/// <para>
/// A signed-in connection joins its user's group and from then on receives that user's turns — and only
/// those — as they are answered (PLAN.md §22): <see cref="ChatEvent"/> for each piece of a reply,
/// <see cref="ChatTurn"/> when a turn starts or ends. Requests do not come in here: a turn is submitted
/// over HTTP (<c>POST /chat/turns</c>), queued, and answered by a worker. What a client missed while it
/// was away it reads over HTTP by sequence number.
/// </para>
/// <see cref="Stream"/> remains as a plain-text way to talk to a model directly, without tools or storage.
/// </summary>
public sealed class ChatHub(IModelProviderResolver resolver) : Hub
{
    private const string UserIdItem = "userId";

    /// <summary>One piece of a turn's reply: <c>{ turnId, sessionId, sequence, chunk }</c>, the chunk as in the UI message stream.</summary>
    public const string ChatEvent = "chatEvent";

    /// <summary>A turn started or ended: <c>{ turnId, sessionId, state, error, replyStored, lastSequence, … }</c>.</summary>
    public const string ChatTurn = "chatTurn";

    public static string GroupFor(string userId) => $"user:{userId}";

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
        // The group is the user's own: which group a connection joins is decided by its token and nothing else.
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(userId), Context.ConnectionAborted);
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
        // The connection's database context is restricted to the user it authenticated as.
        http.RequestServices.GetRequiredService<Cerberus.Data.DataOwner>().RestrictTo(userId);
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
