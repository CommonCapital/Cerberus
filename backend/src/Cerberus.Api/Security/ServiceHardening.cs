using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;

namespace Cerberus.Api.Security;

/// <summary>
/// The service's own limits (PLAN.md §22 item 8, A3), read once at start-up.
/// </summary>
/// <param name="RequestsPerMinutePerAddress">Requests one network address may make in a minute; 0 switches the limit off.</param>
/// <param name="RequestsPerMinutePerUser">Requests one sign-in token may make in a minute; 0 switches the limit off.</param>
/// <param name="MaxRequestBytes">Largest request body accepted.</param>
/// <param name="CorsOrigins">Origins a browser client may call from; empty means none (no cross-origin access).</param>
public sealed record ServiceLimits(
    int RequestsPerMinutePerAddress,
    int RequestsPerMinutePerUser,
    long MaxRequestBytes,
    IReadOnlyList<string> CorsOrigins)
{
    public const string PerAddressVariable = "CERBERUS_RATE_PER_ADDRESS";
    public const string PerUserVariable = "CERBERUS_RATE_PER_USER";
    public const string MaxRequestBytesVariable = "CERBERUS_MAX_REQUEST_BYTES";
    public const string CorsOriginsVariable = "CERBERUS_CORS_ORIGINS";

    public const int DefaultPerAddress = 1200;
    public const int DefaultPerUser = 600;
    public const long DefaultMaxRequestBytes = 16 * 1024 * 1024;

    /// <summary>Call after <see cref="ServiceConfig.Validate"/>: a value that cannot be read falls back to the default here.</summary>
    public static ServiceLimits FromEnvironment(Func<string, string?> env) => new(
        int.TryParse(env(PerAddressVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var perAddress) ? perAddress : DefaultPerAddress,
        int.TryParse(env(PerUserVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var perUser) ? perUser : DefaultPerUser,
        long.TryParse(env(MaxRequestBytesVariable), NumberStyles.None, CultureInfo.InvariantCulture, out var maxBytes) ? maxBytes : DefaultMaxRequestBytes,
        ServiceConfig.SplitOrigins(env(CorsOriginsVariable)));
}

/// <summary>
/// Checks the service's settings before it starts. A setting that is present
/// but cannot be understood stops the service with a message naming it — it is
/// not silently replaced by a default, which for a safety switch (an
/// engagement being mandatory, remote embedding being allowed) would mean
/// running with the opposite of what was asked. Values are never printed: some
/// of them are addresses with credentials in them.
/// </summary>
public static class ServiceConfig
{
    private static readonly string[] Integers =
    [
        "CERBERUS_CHAT_CONCURRENCY", "CERBERUS_CHAT_PER_USER", "CERBERUS_JOB_CONCURRENCY", "CERBERUS_AGENT_PARALLELISM",
    ];

    private static readonly string[] PositiveSeconds =
    [
        "CERBERUS_CHAT_TURN_TIMEOUT_SECONDS", "CERBERUS_JOB_TIMEOUT_SECONDS", "CERBERUS_JOB_GRACE_SECONDS", "CERBERUS_QUESTION_TIMEOUT_SECONDS",
    ];

    private static readonly string[] TrueFalse =
    [
        "CERBERUS_REQUIRE_ENGAGEMENT", "CERBERUS_ALLOW_LOCAL_TARGETS", "CERBERUS_EMBEDDING_ALLOW_REMOTE", "CERBERUS_BROWSER_NO_SANDBOX",
    ];

    private static readonly string[] OnOff = ["CERBERUS_WEB", "CERBERUS_BROWSER"];

    private static readonly string[] HttpUrls = ["CERBERUS_SEARXNG_URL", "CERBERUS_EMBEDDING_URL", "OLLAMA_BASE_URL", "DEEPSEEK_BASE_URL"];

    /// <summary>What is wrong with the settings, one line each; empty when the service may start.</summary>
    public static IReadOnlyList<string> Validate(Func<string, string?> env)
    {
        var errors = new List<string>();
        string? Value(string name) => env(name) is { Length: > 0 } value ? value.Trim() : null;   // empty is unset

        foreach (var name in Integers)
        {
            if (Value(name) is { } v && !int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                errors.Add($"{name} must be a whole number.");
        }
        foreach (var name in PositiveSeconds)
        {
            if (Value(name) is { } v && !(double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0))
                errors.Add($"{name} must be a number of seconds greater than zero (a decimal point, not a comma).");
        }
        foreach (var name in TrueFalse)
        {
            if (Value(name) is { } v && v is not ("true" or "false"))
                errors.Add($"{name} must be exactly \"true\" or \"false\".");
        }
        foreach (var name in OnOff)
        {
            if (Value(name) is { } v && v is not ("on" or "off"))
                errors.Add($"{name} must be exactly \"on\" or \"off\".");
        }
        foreach (var name in HttpUrls)
        {
            if (Value(name) is { } v && !IsUrl(v, "http", "https"))
                errors.Add($"{name} must be an http:// or https:// address.");
        }
        if (Value("CERBERUS_RABBITMQ_URL") is { } broker && !IsUrl(broker, "amqp", "amqps"))
            errors.Add("CERBERUS_RABBITMQ_URL must be an amqp:// or amqps:// address.");

        if (Value("PORT") is { } port && !(int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535))
            errors.Add("PORT must be a number from 1 to 65535.");
        if (Value("CERBERUS_CONTEXT_TOKENS") is { } tokens && !(int.TryParse(tokens, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t >= 1000))
            errors.Add("CERBERUS_CONTEXT_TOKENS must be a whole number of at least 1000.");

        foreach (var name in new[] { ServiceLimits.PerAddressVariable, ServiceLimits.PerUserVariable })
        {
            if (Value(name) is { } v && !int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                errors.Add($"{name} must be a whole number of requests per minute (0 switches the limit off).");
        }
        if (Value(ServiceLimits.MaxRequestBytesVariable) is { } max &&
            !(long.TryParse(max, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) && bytes >= 1024))
        {
            errors.Add($"{ServiceLimits.MaxRequestBytesVariable} must be a whole number of bytes, at least 1024.");
        }

        foreach (var origin in SplitOrigins(env(ServiceLimits.CorsOriginsVariable)))
        {
            if (!IsOrigin(origin))
            {
                errors.Add($"{ServiceLimits.CorsOriginsVariable} must be a comma-separated list of origins such as https://app.example.com — " +
                           "scheme and host, an optional port, no path and no wildcard.");
                break;
            }
        }
        return errors;
    }

    public static IReadOnlyList<string> SplitOrigins(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsUrl(string value, params string[] schemes) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && schemes.Contains(uri.Scheme, StringComparer.Ordinal) && uri.Host.Length > 0;

    /// <summary>Exactly what a browser sends as Origin: scheme://host[:port], nothing after it.</summary>
    private static bool IsOrigin(string value) =>
        !value.Contains('*') && IsUrl(value, "http", "https") &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        string.Equals(uri.GetLeftPart(UriPartial.Authority), value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Protection of the service itself, as opposed to what it scans: response
/// headers, a cap on request size, request-rate limits per network address and
/// per sign-in token, and a list of the origins a browser may call from.
///
/// What the rate limits are not: a defence against a distributed flood (that is
/// the job of what sits in front of the service), and — behind a reverse proxy
/// — a per-client limit, because the address seen is then the proxy's. One
/// person signed in twice has two tokens and so twice the per-user allowance.
/// Counted in this process; several instances each count on their own (§22).
/// </summary>
public static class ServiceHardening
{
    /// <summary>Paths that are never limited: a container's liveness probe must not be refused.</summary>
    private static bool IsExempt(HttpContext http) => http.Request.Path.StartsWithSegments("/health");

    public static void AddServiceHardening(this IServiceCollection services, ServiceLimits limits)
    {
        services.AddSingleton(limits);
        if (limits.CorsOrigins.Count > 0) services.AddCors();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, ct) =>
            {
                var wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : TimeSpan.FromSeconds(10);
                context.HttpContext.Response.Headers.RetryAfter =
                    Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await context.HttpContext.Response.WriteAsJsonAsync(new { error = "Too many requests. Try again shortly." }, ct);
            };

            // Both must allow a request: the address it comes from, and the sign-in token it carries.
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(http =>
                    limits.RequestsPerMinutePerAddress <= 0 || IsExempt(http)
                        ? RateLimitPartition.GetNoLimiter("-")
                        : PerMinute("a:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"), limits.RequestsPerMinutePerAddress)),
                PartitionedRateLimiter.Create<HttpContext, string>(http =>
                    limits.RequestsPerMinutePerUser <= 0 || IsExempt(http) ||
                    Auth.RequireAuthFilter.ExtractBearerToken(http.Request.Headers.Authorization) is not { } token
                        ? RateLimitPartition.GetNoLimiter("-")
                        : PerMinute("u:" + Fingerprint(token), limits.RequestsPerMinutePerUser)));
        });
    }

    private static RateLimitPartition<string> PerMinute(string key, int permits) =>
        RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
            AutoReplenishment = true,
        });

    /// <summary>The token is not kept as a key: a digest tells tokens apart just as well.</summary>
    private static string Fingerprint(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)), 0, 16);

    /// <summary>
    /// Headers on every response, errors included — so this goes first in the
    /// pipeline. The API serves data, never a page: nothing may be framed,
    /// sniffed, cached or loaded from it by a browser.
    /// </summary>
    public static void UseSecurityHeaders(this WebApplication app)
    {
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.TryAdd("X-Content-Type-Options", "nosniff");
                headers.TryAdd("X-Frame-Options", "DENY");
                headers.TryAdd("Referrer-Policy", "no-referrer");
                headers.TryAdd("Content-Security-Policy", "default-src 'none'; frame-ancestors 'none'");
                headers.TryAdd("Cache-Control", "no-store");
                return Task.CompletedTask;
            });
            return next(context);
        });
    }

    /// <summary>Cross-origin rules, the rate limits and the request-size cap, in that order.</summary>
    public static void UseServiceHardening(this WebApplication app)
    {
        var limits = app.Services.GetRequiredService<ServiceLimits>();

        if (limits.CorsOrigins.Count > 0)
        {
            // Named origins only, so credentials may be allowed (the SignalR browser client sends them).
            app.UseCors(policy => policy.WithOrigins([.. limits.CorsOrigins]).AllowAnyHeader().AllowAnyMethod().AllowCredentials());
        }

        // Counted first, so that a stream of oversized requests is limited like any other.
        app.UseRateLimiter();

        app.Use(async (context, next) =>
        {
            var tooLarge = new { error = $"Request body too large (the limit is {limits.MaxRequestBytes} bytes)." };

            // The server enforces it while reading, also for a body sent without a length.
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = limits.MaxRequestBytes;

            if (context.Request.ContentLength > limits.MaxRequestBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await context.Response.WriteAsJsonAsync(tooLarge);
                return;
            }
            await next(context);

            // A body without a length that ran over the cap while being read: the framework answers 413
            // with nothing in it. Clients of this API expect JSON errors.
            if (context.Response.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
                await context.Response.WriteAsJsonAsync(tooLarge);
        });
    }
}
