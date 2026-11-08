using System.Text;
using System.Text.Json;
using Cerberus.Api.Auth;
using Cerberus.Api.Chat;
using Cerberus.Api.Settings;
using Cerberus.Core.Models;
using Cerberus.Data;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api;

public static class Endpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static void MapCerberusEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));

        var auth = app.MapGroup("/auth");
        auth.MapPost("/register", Register);
        auth.MapPost("/login", Login);
        auth.MapPost("/logout", Logout);

        var sessions = app.MapGroup("/sessions").AddEndpointFilter<RequireAuthFilter>();
        sessions.MapGet("/", ListSessions);
        sessions.MapGet("/{id}", GetSession);
        sessions.MapPost("/", CreateSession);

        var settings = app.MapGroup("/settings/keys").AddEndpointFilter<RequireAuthFilter>();
        settings.MapGet("/", ListApiKeys);
        settings.MapPut("/", SetApiKey);
        settings.MapDelete("/{provider}", DeleteApiKey);

        app.MapPost("/chat", StreamChat).AddEndpointFilter<RequireAuthFilter>();
    }

    // --- settings: per-user model API keys ----------------------------------

    private static async Task<IResult> ListApiKeys(HttpContext http, ApiKeyService keys, CancellationToken ct)
    {
        var providers = await keys.ListProvidersAsync(UserId(http), ct);
        // Never return the key material — only which providers are configured.
        var result = ApiKeyService.SupportedProviders
            .Select(p => new { provider = p, hasKey = providers.Contains(p) });
        return Results.Json(result);
    }

    private static async Task<IResult> SetApiKey(SetApiKeyRequest body, HttpContext http, ApiKeyService keys, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Provider) || !ApiKeyService.IsSupportedProvider(body.Provider))
        {
            return Results.Json(new { error = $"Unsupported provider. Use one of: {string.Join(", ", ApiKeyService.SupportedProviders)}" }, statusCode: 400);
        }
        if (string.IsNullOrWhiteSpace(body.ApiKey))
        {
            return Results.Json(new { error = "apiKey is required" }, statusCode: 400);
        }

        await keys.SetAsync(UserId(http), body.Provider, body.ApiKey, ct);
        return Results.Json(new { provider = body.Provider, hasKey = true });
    }

    private static async Task<IResult> DeleteApiKey(string provider, HttpContext http, ApiKeyService keys, CancellationToken ct)
    {
        var removed = await keys.DeleteAsync(UserId(http), provider, ct);
        return removed
            ? Results.Json(new { provider, hasKey = false })
            : Results.Json(new { error = "No key set for that provider" }, statusCode: 404);
    }

    // --- auth ---------------------------------------------------------------

    private static async Task<IResult> Register(CredentialsRequest body, AuthService auth, CancellationToken ct)
    {
        if (!IsValid(body))
        {
            return Results.Json(new { error = "A valid email and a password (min 6 characters) are required" }, statusCode: 400);
        }
        try
        {
            var token = await auth.RegisterAsync(body.Email, body.Password, ct);
            return Results.Json(new { token }, statusCode: 201);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 409);
        }
    }

    private static async Task<IResult> Login(CredentialsRequest body, AuthService auth, CancellationToken ct)
    {
        if (!IsValid(body))
        {
            return Results.Json(new { error = "A valid email and a password (min 6 characters) are required" }, statusCode: 400);
        }
        var token = await auth.LoginAsync(body.Email, body.Password, ct);
        return token is null
            ? Results.Json(new { error = "Invalid email or password" }, statusCode: 401)
            : Results.Json(new { token });
    }

    private static async Task<IResult> Logout(HttpContext http, AuthService auth, CancellationToken ct)
    {
        var token = RequireAuthFilter.ExtractBearerToken(http.Request.Headers.Authorization);
        if (token is not null) await auth.LogoutAsync(token, ct);
        return Results.Json(new { success = true });
    }

    private static bool IsValid(CredentialsRequest body) =>
        !string.IsNullOrWhiteSpace(body.Email)
        && body.Email.Contains('@')
        && body.Password?.Length >= 6;

    // --- sessions -----------------------------------------------------------

    private static async Task<IResult> ListSessions(HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var sessions = await db.Sessions
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.Id, s.Title, s.CreatedAt })
            .ToListAsync(ct);
        return Results.Json(sessions);
    }

    private static async Task<IResult> GetSession(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (session is null) return Results.Json(new { error = "Session not found" }, statusCode: 404);

        return Results.Json(new
        {
            session.Id,
            session.UserId,
            session.Title,
            session.CreatedAt,
            session.UpdatedAt,
            messages = ParseStoredMessages(session.Messages),
        });
    }

    private static async Task<IResult> CreateSession(CreateSessionRequest body, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var session = new Session { UserId = userId, Title = body.Title };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Results.Json(new
        {
            session.Id,
            session.UserId,
            session.Title,
            session.CreatedAt,
            session.UpdatedAt,
            messages = Array.Empty<object>(),
        }, statusCode: 201);
    }

    // --- chat (AI-SDK UI message stream) ------------------------------------

    private static async Task StreamChat(
        UiChatRequest body, HttpContext http, CerberusDbContext db, IModelProviderResolver resolver,
        ApiKeyService keys, CancellationToken ct)
    {
        var userId = UserId(http);

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == body.Id && s.UserId == userId, ct);
        if (session is null)
        {
            http.Response.StatusCode = 404;
            await http.Response.WriteAsJsonAsync(new { error = "Session not found" }, ct);
            return;
        }

        if (!ModelRegistry.IsSupported(body.Model))
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = $"Unsupported model: {body.Model}" }, ct);
            return;
        }

        var history = ParseStoredMessages(session.Messages);
        var promptMessages = BuildPromptMessages(history, body.Messages);
        var system = SystemPrompt.Build(body.Mode);

        UiMessageStreamWriter.ApplyHeaders(http.Response);
        var writer = new UiMessageStreamWriter(http.Response);

        var assistantId = Guid.NewGuid().ToString("n");
        const string textId = "0";
        var startedAt = DateTime.UtcNow;
        var assistant = new StringBuilder();

        await writer.StartAsync(assistantId, new { mode = body.Mode, model = body.Model }, ct);
        await writer.StartStepAsync(ct);

        try
        {
            var provider = resolver.Resolve(body.Model);
            var credentials = await keys.ResolveCredentialsAsync(userId, ct);
            var completion = new ChatCompletionRequest(body.Model, promptMessages, system, credentials);

            await writer.TextStartAsync(textId, ct);
            await foreach (var delta in provider.StreamAsync(completion, ct))
            {
                assistant.Append(delta);
                await writer.TextDeltaAsync(textId, delta, ct);
            }
            await writer.TextEndAsync(textId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await writer.ErrorAsync(ex.Message, ct);
            await writer.DoneAsync(ct);
            return;
        }

        await writer.FinishStepAsync(ct);
        var durationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
        await writer.FinishAsync(new { mode = body.Mode, model = body.Model, durationMs }, ct);

        await PersistAsync(db, session, history, body, assistantId, assistant.ToString(), durationMs, ct);

        await writer.DoneAsync(ct);
    }

    private static async Task PersistAsync(
        CerberusDbContext db, Session session, List<JsonElement> history, UiChatRequest body,
        string assistantId, string assistantText, long durationMs, CancellationToken ct)
    {
        var merged = new List<object>();
        foreach (var h in history) merged.Add(h);
        foreach (var m in body.Messages)
        {
            merged.Add(new
            {
                id = m.Id,
                role = m.Role,
                parts = m.Parts.Select(p => new { type = p.Type, text = p.Text }),
            });
        }
        merged.Add(new
        {
            id = assistantId,
            role = "assistant",
            parts = new[] { new { type = "text", text = assistantText } },
            metadata = new { mode = body.Mode, model = body.Model, durationMs },
        });

        session.Messages = JsonSerializer.Serialize(merged, JsonOptions);
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // --- helpers ------------------------------------------------------------

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static List<JsonElement> ParseStoredMessages(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            }
        }
        catch (JsonException)
        {
            // fall through
        }
        return [];
    }

    private static List<ChatMessage> BuildPromptMessages(List<JsonElement> history, List<UiMessage> incoming)
    {
        var result = new List<ChatMessage>();
        foreach (var h in history)
        {
            var text = ExtractText(h);
            if (!string.IsNullOrEmpty(text)) result.Add(new ChatMessage(RoleOf(h), text));
        }
        foreach (var m in incoming)
        {
            var text = string.Concat(m.Parts.Where(p => p.Type == "text").Select(p => p.Text));
            if (!string.IsNullOrEmpty(text)) result.Add(new ChatMessage(m.Role, text));
        }
        return result;
    }

    private static string ExtractText(JsonElement message)
    {
        if (!message.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                sb.Append(text.GetString());
            }
        }
        return sb.ToString();
    }

    private static string RoleOf(JsonElement message) =>
        message.TryGetProperty("role", out var role) ? role.GetString() ?? "user" : "user";
}
