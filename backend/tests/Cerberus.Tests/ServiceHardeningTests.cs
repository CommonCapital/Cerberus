using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Cerberus.Api.Security;

namespace Cerberus.Tests;

/// <summary>The settings check that runs before the service starts.</summary>
public class ServiceConfigTests
{
    private static IReadOnlyList<string> Validate(params (string Name, string Value)[] settings)
    {
        var map = settings.ToDictionary(s => s.Name, s => s.Value);
        return ServiceConfig.Validate(name => map.GetValueOrDefault(name));
    }

    [Fact]
    public void Nothing_set_is_valid()
    {
        Assert.Empty(Validate());
    }

    [Fact]
    public void Well_formed_settings_are_valid()
    {
        Assert.Empty(Validate(
            ("PORT", "3000"), ("CERBERUS_JOB_CONCURRENCY", "4"), ("CERBERUS_JOB_TIMEOUT_SECONDS", "0.6"),
            ("CERBERUS_REQUIRE_ENGAGEMENT", "true"), ("CERBERUS_WEB", "off"), ("CERBERUS_BROWSER", "on"),
            ("CERBERUS_SEARXNG_URL", "http://search.invalid"), ("OLLAMA_BASE_URL", "http://localhost:11434/v1"),
            ("CERBERUS_RABBITMQ_URL", "amqps://svc:pw@mq.example.com/vhost"), ("CERBERUS_CONTEXT_TOKENS", "8000"),
            ("CERBERUS_RATE_PER_ADDRESS", "0"), ("CERBERUS_RATE_PER_USER", "300"), ("CERBERUS_MAX_REQUEST_BYTES", "1048576"),
            ("CERBERUS_CORS_ORIGINS", "https://app.example.com, http://localhost:5173"),
            // Present but empty is the same as not set.
            ("CERBERUS_EMBEDDING_URL", "")));
    }

    [Theory]
    // A safety switch written loosely must not quietly mean "off".
    [InlineData("CERBERUS_REQUIRE_ENGAGEMENT", "True")]
    [InlineData("CERBERUS_REQUIRE_ENGAGEMENT", "1")]
    [InlineData("CERBERUS_EMBEDDING_ALLOW_REMOTE", "yes")]
    [InlineData("CERBERUS_WEB", "true")]
    [InlineData("CERBERUS_BROWSER", "ON")]
    [InlineData("CERBERUS_JOB_CONCURRENCY", "two")]
    [InlineData("CERBERUS_JOB_TIMEOUT_SECONDS", "0")]
    [InlineData("CERBERUS_JOB_TIMEOUT_SECONDS", "1,5")]
    [InlineData("CERBERUS_CONTEXT_TOKENS", "500")]
    [InlineData("PORT", "70000")]
    [InlineData("PORT", "http")]
    [InlineData("CERBERUS_SEARXNG_URL", "search.internal")]
    [InlineData("OLLAMA_BASE_URL", "ftp://models")]
    [InlineData("CERBERUS_RABBITMQ_URL", "http://mq.example.com")]
    [InlineData("CERBERUS_RATE_PER_ADDRESS", "-1")]
    [InlineData("CERBERUS_RATE_PER_USER", "many")]
    [InlineData("CERBERUS_MAX_REQUEST_BYTES", "10")]
    [InlineData("CERBERUS_CORS_ORIGINS", "*")]
    [InlineData("CERBERUS_CORS_ORIGINS", "https://*.example.com")]
    [InlineData("CERBERUS_CORS_ORIGINS", "https://app.example.com/path")]
    [InlineData("CERBERUS_CORS_ORIGINS", "https://app.example.com/")]
    [InlineData("CERBERUS_CORS_ORIGINS", "app.example.com")]
    [InlineData("CERBERUS_CORS_ORIGINS", "https://ok.example.com, null")]
    public void A_setting_that_cannot_be_understood_is_named(string name, string value)
    {
        var error = Assert.Single(Validate((name, value)));
        Assert.StartsWith(name, error);
    }

    [Fact]
    public void Values_are_never_repeated_in_the_message()
    {
        // An address may carry a password.
        var error = Assert.Single(Validate(("CERBERUS_RABBITMQ_URL", "amqp//svc:hunter2pass@mq")));
        Assert.DoesNotContain("hunter2pass", error);
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        Assert.Equal(3, Validate(("PORT", "x"), ("CERBERUS_WEB", "yes"), ("CERBERUS_JOB_CONCURRENCY", "x")).Count);
    }

    [Fact]
    public void Limits_have_defaults_and_read_what_is_set()
    {
        var defaults = ServiceLimits.FromEnvironment(_ => null);
        Assert.Equal(ServiceLimits.DefaultPerAddress, defaults.RequestsPerMinutePerAddress);
        Assert.Equal(ServiceLimits.DefaultPerUser, defaults.RequestsPerMinutePerUser);
        Assert.Equal(ServiceLimits.DefaultMaxRequestBytes, defaults.MaxRequestBytes);
        Assert.Empty(defaults.CorsOrigins);

        var set = ServiceLimits.FromEnvironment(name => name switch
        {
            "CERBERUS_RATE_PER_ADDRESS" => "0",
            "CERBERUS_RATE_PER_USER" => "30",
            "CERBERUS_MAX_REQUEST_BYTES" => "2048",
            "CERBERUS_CORS_ORIGINS" => "https://a.example.com,https://b.example.com",
            _ => null,
        });
        Assert.Equal((0, 30, 2048L), (set.RequestsPerMinutePerAddress, set.RequestsPerMinutePerUser, set.MaxRequestBytes));
        Assert.Equal(["https://a.example.com", "https://b.example.com"], set.CorsOrigins);
    }
}

/// <summary>The service's own protection, through a running API.</summary>
[Collection("api")]
public class ServiceHardeningTests(ApiHost host)
{
    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values) : "";

    [Fact]
    public async Task Every_response_carries_the_security_headers_errors_included()
    {
        using var client = host.CreateClient();
        foreach (var response in new[]
                 {
                     await client.GetAsync("/health"),
                     await client.GetAsync("/sessions"),                 // 401
                     await client.GetAsync("/no-such-endpoint"),         // 404
                     await client.PostAsync("/auth/login", new StringContent("{not json", Encoding.UTF8, "application/json")),   // 400
                 })
        {
            Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
            Assert.Equal("DENY", Header(response, "X-Frame-Options"));
            Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
            Assert.Equal("default-src 'none'; frame-ancestors 'none'", Header(response, "Content-Security-Policy"));
            Assert.Equal("no-store", Header(response, "Cache-Control"));
            Assert.Equal("", Header(response, "Server"));
        }
    }

    [Fact]
    public async Task By_default_no_browser_origin_is_allowed()
    {
        using var client = host.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", "https://evil.example.com");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("", Header(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Only_a_listed_origin_is_answered_with_permission()
    {
        using var listed = host.StartVariant("CERBERUS_CORS_ORIGINS", "https://app.example.com");
        using var client = listed.CreateClient();

        async Task<HttpResponseMessage> Preflight(string origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Options, "/sessions");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "GET");
            request.Headers.Add("Access-Control-Request-Headers", "authorization");
            return await client.SendAsync(request);
        }

        var allowed = await Preflight("https://app.example.com");
        Assert.Equal("https://app.example.com", Header(allowed, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Header(allowed, "Access-Control-Allow-Credentials"));

        var refused = await Preflight("https://evil.example.com");
        Assert.Equal("", Header(refused, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_request_body_over_the_limit_is_refused_before_it_is_read()
    {
        using var small = host.StartVariant("CERBERUS_MAX_REQUEST_BYTES", "2048");
        using var client = small.CreateClient();

        var tooLarge = await client.PostAsJsonAsync("/auth/register", new { email = "a@test.dev", password = new string('x', 5000) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Contains("too large", (await tooLarge.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var fine = await client.PostAsJsonAsync("/auth/register", new { email = $"{Guid.NewGuid():n}@test.dev", password = "secret123" });
        Assert.Equal(HttpStatusCode.Created, fine.StatusCode);
    }

    [Fact]
    public async Task One_address_is_refused_after_its_allowance_but_the_liveness_probe_never_is()
    {
        using var limited = host.StartVariant("CERBERUS_RATE_PER_ADDRESS", "5");
        using var client = limited.CreateClient();

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/sessions")).StatusCode);

        var refused = await client.GetAsync("/sessions");
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(int.Parse(Header(refused, "Retry-After")) >= 1);
        Assert.Contains("Too many requests", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal("nosniff", Header(refused, "X-Content-Type-Options"));

        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task One_user_is_refused_after_their_allowance_and_another_is_not_affected()
    {
        using var limited = host.StartVariant("CERBERUS_RATE_PER_USER", "3");
        using var first = await ApiHost.AuthenticatedClientAsync(limited);
        using var second = await ApiHost.AuthenticatedClientAsync(limited);

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await first.GetAsync("/sessions")).StatusCode);

        // Another person's allowance is their own, and requests without a token are not counted against anyone's.
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/sessions")).StatusCode);
        using var anonymous = limited.CreateClient();
        for (var i = 0; i < 6; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/sessions")).StatusCode);

        // A made-up token gets its own small allowance: it cannot be used to exhaust a real user's.
        using var guesser = limited.CreateClient();
        guesser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await guesser.GetAsync("/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await guesser.GetAsync("/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/sessions")).StatusCode);
    }
}
