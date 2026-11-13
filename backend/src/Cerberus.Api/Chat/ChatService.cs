using System.Text.Json;
using System.Text.Json.Nodes;
using Cerberus.Api.Settings;
using Cerberus.Core.Models;
using Cerberus.Data;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Chat;

/// <summary>Why a chat request was not taken, as an HTTP client is told.</summary>
public sealed record ChatProblem(int Status, string Error);

/// <summary>A chat request that passed its checks, with everything a reply needs. Belongs to the service that prepared it.</summary>
public sealed class PreparedChat
{
    internal PreparedChat(Session session, ChatRun run)
    {
        Session = session;
        Run = run;
    }

    internal Session Session { get; }
    internal ChatRun Run { get; }
}

/// <summary>
/// One reply of the chat agent, from request to stored conversation — whoever carries it: the HTTP endpoint
/// that streams the reply as its response, or a worker that took the request from the queue and delivers the
/// reply over SignalR (PLAN.md §22). Both get the same checks, the same conversation and the same storage,
/// always for the user the caller names: a conversation is only ever found under its owner's id.
/// </summary>
public sealed class ChatService(
    CerberusDbContext db, ChatRunner runner, ApiKeyService keys, ChatCompactor compactor, ChatLimits limits, ILogger<ChatService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>What can be said about a request without looking anything up: its shape and its model.</summary>
    public static ChatProblem? Check(UiChatRequest body)
    {
        var mode = body.Mode?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(body.Id) || body.Messages is not { Count: > 0 } ||
            !body.Messages.All(UiMessages.IsValid) || !ChatTools.IsValidMode(mode))
        {
            return new ChatProblem(400, "Invalid request body");
        }
        if (string.IsNullOrWhiteSpace(body.Model) || !ModelRegistry.IsSupported(body.Model))
        {
            return new ChatProblem(400, $"Unsupported model: {body.Model}. Give {ModelRegistry.Accepted}.");
        }
        return null;
    }

    /// <summary>
    /// Checks the request and gets the conversation ready: the stored messages with the incoming ones merged in,
    /// the user's own keys and memory, and — if the conversation is about to outgrow the model — its summary.
    /// Nothing has been written to any stream when this returns a problem.
    /// </summary>
    public async Task<(ChatProblem? Problem, PreparedChat? Prepared)> PrepareAsync(string userId, UiChatRequest body, CancellationToken ct)
    {
        if (Check(body) is { } problem) return (problem, null);
        var mode = body.Mode!.Trim().ToUpperInvariant();

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == body.Id && s.UserId == userId, ct);
        if (session is null) return (new ChatProblem(404, "Session not found"), null);

        // A helper only ever looks: whatever mode the request names, it gets the read-only tools.
        var helper = session.ParentId is not null;
        if (helper) mode = ChatTools.ModePlan;

        // Incoming messages replace stored ones with the same id, and are stamped with the mode and model in use.
        var messages = UiMessages.Merge(Endpoints.ParseStoredMessages(session.Messages), body.Messages!);
        var incomingIds = body.Messages!.Select(m => m.GetProperty("id").GetString()).ToHashSet();
        foreach (var message in messages.Where(m => incomingIds.Contains(UiMessages.IdOf(m))))
        {
            var metadata = message["metadata"] as JsonObject ?? [];
            metadata["mode"] = mode;
            metadata["model"] = body.Model;
            message["metadata"] = metadata;
        }

        var credentials = await keys.ResolveCredentialsAsync(userId, ct);
        // What the operator asked to be remembered: their instructions and their notes about no target in particular.
        var memory = await Memory.PromptSectionAsync(db, userId, engagementId: null, targetPath: null, ct);

        // Compaction: a new user message on a conversation that is about to outgrow the model's window
        // has its earlier part summarised first. A reply in progress (tool results coming back) is never interrupted for it.
        var notices = new List<(string Kind, object Data)>();
        if (UiMessages.RoleOf(messages[^1]) == "user" &&
            ChatCompactor.IsDue(messages, session.Summary, session.SummarizedThrough, body.Model!, limits))
        {
            try
            {
                if (await compactor.CompactAsync(messages, session.Summary, session.SummarizedThrough, body.Model!, credentials, ct) is { } compaction)
                {
                    session.Summary = compaction.Summary;
                    session.SummarizedThrough = compaction.Through;
                    await db.SaveChangesAsync(ct);
                    notices.Add(("compaction", new { messages = compaction.Messages }));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The reply is attempted anyway; if the conversation really is too long, the provider will say so.
                logger.LogWarning(ex, "Could not compact session {SessionId}.", session.Id);
            }
        }

        return (null, new PreparedChat(session, new ChatRun(
            mode, body.Model!, messages, credentials, memory, ChatContext.Read(body.Context).PromptSection(),
            session.Summary, session.SummarizedThrough, notices, session.Tasks,
            Delegation: body.Subagents == true, Helper: helper)));
    }

    /// <summary>Writes the reply to the writer and keeps what is to be kept. Returns what the reply came to.</summary>
    public async Task<ChatResult> RunAsync(PreparedChat prepared, UiMessageStreamWriter writer, CancellationToken ct)
    {
        var session = prepared.Session;
        var result = await runner.RunAsync(writer, prepared.Run, ct);

        // The task list is kept as soon as it changes — also when the reply is waiting on client tools:
        // the continuation is a new request and must find the list as the model left it.
        if (result.Tasks is not null)
        {
            session.Tasks = result.Tasks;
            if (!result.Complete) await db.SaveChangesAsync(CancellationToken.None);
        }

        // Stored only once the reply is whole. While tool calls are waiting for the
        // client, the client holds the message and sends it back with the results.
        if (result.Complete)
        {
            session.Messages = JsonSerializer.Serialize(result.Messages, JsonOptions);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        return result;
    }
}
