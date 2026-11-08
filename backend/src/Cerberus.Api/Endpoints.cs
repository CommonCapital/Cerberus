using System.Text.Json;
using Cerberus.Api.Auth;
using Cerberus.Core.Models;
using Cerberus.Data;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api;

public static class Endpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
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

        app.MapPost("/chat", StreamChat).AddEndpointFilter<RequireAuthFilter>();
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
            messages = ParseMessages(session.Messages),
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

    // --- chat ---------------------------------------------------------------

    private static async Task StreamChat(
        ChatRequest body, HttpContext http, CerberusDbContext db, IModelProviderResolver resolver, CancellationToken ct)
    {
        var userId = UserId(http);

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == body.SessionId && s.UserId == userId, ct);
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

        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";

        var history = ParseMessages(session.Messages);
        var incoming = body.Messages;
        var promptMessages = history
            .Concat(incoming)
            .Select(m => new ChatMessage(m.Role, m.Content))
            .ToList();

        var completion = new ChatCompletionRequest(body.Model, promptMessages, body.System, new ModelCredentials());
        var provider = resolver.Resolve(body.Model);

        var assistant = new System.Text.StringBuilder();
        try
        {
            await foreach (var delta in provider.StreamAsync(completion, ct))
            {
                assistant.Append(delta);
                await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { delta }, JsonOptions)}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { error = ex.Message }, JsonOptions)}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
            return;
        }

        var updated = history
            .Concat(incoming)
            .Append(new ChatRequestMessage("assistant", assistant.ToString()))
            .ToList();
        session.Messages = JsonSerializer.Serialize(updated, JsonOptions);
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await http.Response.WriteAsync("data: [DONE]\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    // --- helpers ------------------------------------------------------------

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static List<ChatRequestMessage> ParseMessages(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ChatRequestMessage>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
