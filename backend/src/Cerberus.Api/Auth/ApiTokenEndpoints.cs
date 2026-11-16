using Cerberus.Api.Audit;
using Cerberus.Data;

namespace Cerberus.Api.Auth;

public sealed record CreateApiTokenRequest(string? Name, int? ExpiresInDays = null);

/// <summary>Named credentials for unattended REST clients. Only a password sign-in session can manage them.</summary>
public static class ApiTokenEndpoints
{
    public static void MapApiTokenEndpoints(this WebApplication app)
    {
        var tokens = app.MapGroup("/auth/tokens")
            .AddEndpointFilter<RequireAuthFilter>()
            .AddEndpointFilter<RequireSignInSession>();
        tokens.MapGet("/", List);
        tokens.MapPost("/", Create);
        tokens.MapDelete("/{id}", Revoke);
    }

    private static async Task<IResult> List(HttpContext http, ApiTokenService tokens, CancellationToken ct) =>
        Results.Json((await tokens.ListAsync(UserId(http), ct)).Select(Describe));

    private static async Task<IResult> Create(
        CreateApiTokenRequest body, HttpContext http, ApiTokenService tokens, AuditLog audit, CancellationToken ct)
    {
        var name = body.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > ApiTokenService.MaxNameLength || body.Name!.Any(char.IsControl))
            return Results.Json(new { error = "name must contain 1 to 100 characters without control characters" }, statusCode: 400);
        if (body.ExpiresInDays is < ApiTokenService.MinExpiryDays or > ApiTokenService.MaxExpiryDays)
            return Results.Json(new { error = "expiresInDays must be between 1 and 365" }, statusCode: 400);

        var userId = UserId(http);
        var created = await tokens.CreateAsync(userId, name, body.ExpiresInDays, ct);
        try
        {
            // Names are supplied by callers and may themselves contain sensitive text. Record only the id.
            await audit.AppendAsync(userId, "api-token.create", AuditOutcome.Ok,
                $"Created API token {created.Info.Id}.", ct: ct);
        }
        catch
        {
            // Never issue a live credential if recording its creation failed.
            await tokens.RevokeAsync(userId, created.Info.Id, CancellationToken.None);
            throw;
        }

        return Results.Json(new
        {
            created.Info.Id, created.Info.Name,
            prefix = created.Info.TokenPrefix,
            createdAt = StoredTime.Utc(created.Info.CreatedAt),
            expiresAt = StoredTime.Utc(created.Info.ExpiresAt),
            revokedAt = created.Info.RevokedAt is { } revoked ? StoredTime.Utc(revoked) : (DateTime?)null,
            created.Token,
        }, statusCode: 201);
    }

    private static async Task<IResult> Revoke(
        string id, HttpContext http, ApiTokenService tokens, AuditLog audit, CancellationToken ct)
    {
        var userId = UserId(http);
        if (!await tokens.RevokeAsync(userId, id, ct))
            return Results.Json(new { error = "API token not found" }, statusCode: 404);
        await audit.AppendAsync(userId, "api-token.revoke", AuditOutcome.Ok, $"Revoked API token {id}.", ct: ct);
        return Results.NoContent();
    }

    private static object Describe(ApiTokenInfo info) => new
    {
        info.Id, info.Name,
        prefix = info.TokenPrefix,
        createdAt = StoredTime.Utc(info.CreatedAt),
        expiresAt = StoredTime.Utc(info.ExpiresAt),
        revokedAt = info.RevokedAt is { } revoked ? StoredTime.Utc(revoked) : (DateTime?)null,
    };

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;
}

/// <summary>A stolen automation credential must not be able to mint a replacement or manage other credentials.</summary>
public sealed class RequireSignInSession : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        var bearer = RequireAuthFilter.ExtractBearerToken(http.Request.Headers.Authorization);
        var auth = http.RequestServices.GetRequiredService<AuthService>();
        if (bearer is null || await auth.ValidateSessionTokenAsync(bearer, http.RequestAborted) !=
            http.Items[RequireAuthFilter.UserIdItem] as string)
            return Results.Json(new { error = "Sign in with your password to manage API tokens." }, statusCode: 403);
        return await next(context);
    }
}
