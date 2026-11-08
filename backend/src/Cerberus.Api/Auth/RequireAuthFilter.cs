namespace Cerberus.Api.Auth;

/// <summary>
/// Endpoint filter that validates the Bearer token and stashes the user id in
/// HttpContext.Items under "userId".
/// </summary>
public sealed class RequireAuthFilter : IEndpointFilter
{
    public const string UserIdItem = "userId";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var token = ExtractBearerToken(http.Request.Headers.Authorization);
        if (token is null)
        {
            return Results.Json(new { error = "Unauthorized. Run /login to continue." }, statusCode: 401);
        }

        var auth = http.RequestServices.GetRequiredService<AuthService>();
        var userId = await auth.ValidateTokenAsync(token, http.RequestAborted);
        if (userId is null)
        {
            return Results.Json(new { error = "Unauthorized. Run /login to continue." }, statusCode: 401);
        }

        http.Items[UserIdItem] = userId;
        return await next(context);
    }

    public static string? ExtractBearerToken(string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var token = header[prefix.Length..].Trim();
        return token.Length == 0 ? null : token;
    }
}
