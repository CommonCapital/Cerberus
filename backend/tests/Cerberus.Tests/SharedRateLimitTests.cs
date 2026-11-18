using System.Net;
using System.Net.Http.Json;
using Cerberus.Api.Cluster;
using Cerberus.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cerberus.Tests;

/// <summary>The request-rate limits counted in a real Redis, as several instances of the service would count them.</summary>
public sealed class SharedRateLimitTests(RedisServer redis) : IClassFixture<RedisServer>
{
    private static ServiceLimits Limits(int perAddress, int perUser) =>
        ServiceLimits.FromEnvironment(name => name switch
        {
            ServiceLimits.PerAddressVariable => perAddress.ToString(),
            ServiceLimits.PerUserVariable => perUser.ToString(),
            _ => null,
        });

    /// <summary>A request as the limiter sees it: from an address, with a token or without one, to a path.</summary>
    private static HttpContext Request(string address, string? token = null, string path = "/jobs")
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(address);
        http.Request.Path = path;
        if (token is not null) http.Request.Headers.Authorization = $"Bearer {token}";
        return http;
    }

    private SharedRateLimit Instance(ServiceLimits limits, RedisLink? link = null, TimeSpan? segment = null) =>
        new(link ?? redis.ConnectedLinkAsync().GetAwaiter().GetResult(), limits, NullLogger<SharedRateLimit>.Instance, segment);

    /// <summary>A unique address per test, so the tests do not share counts in the one Redis.</summary>
    private static string Address() => $"10.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}";

    [RedisFact]
    public async Task The_limit_holds_over_every_instance_not_once_per_instance()
    {
        var limits = Limits(perAddress: 6, perUser: 0);
        var first = Instance(limits);
        var second = Instance(limits);
        var from = Address();

        // Six requests from one address, spread over two instances: all allowed. The seventh, on either, is not.
        for (var i = 0; i < 6; i++) Assert.True((await (i % 2 == 0 ? first : second).DecideAsync(Request(from))).Allowed, $"request {i + 1}");
        var refused = await first.DecideAsync(Request(from));
        Assert.False(refused.Allowed);
        Assert.InRange(refused.RetryAfter, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(10));
        Assert.False((await second.DecideAsync(Request(from))).Allowed);

        // Another address has its own count.
        Assert.True((await second.DecideAsync(Request(Address()))).Allowed);
    }

    [RedisFact]
    public async Task A_request_needs_room_under_both_limits_and_a_refused_one_uses_up_neither()
    {
        var limits = Limits(perAddress: 3, perUser: 2);
        var instance = Instance(limits);
        var office = Address();

        // Two requests with Alice's token use up her allowance — and two of the office's three.
        Assert.True((await instance.DecideAsync(Request(office, "alice-token"))).Allowed);
        Assert.True((await instance.DecideAsync(Request(office, "alice-token"))).Allowed);
        Assert.False((await instance.DecideAsync(Request(office, "alice-token"))).Allowed);   // her limit

        // That refusal did not count against the office: Bob, from the same address, still gets the third place.
        Assert.True((await instance.DecideAsync(Request(office, "bob-token"))).Allowed);
        Assert.False((await instance.DecideAsync(Request(office, "bob-token"))).Allowed);     // now the address's limit

        // Alice from somewhere else is still at her own limit: it follows the token, not the place.
        Assert.False((await instance.DecideAsync(Request(Address(), "alice-token"))).Allowed);
    }

    [RedisFact]
    public async Task The_window_slides_and_the_liveness_probe_is_never_counted()
    {
        // Segments short enough that a whole window passes during the test, and long enough that three
        // requests in a row fall in the same one on a busy CI machine (with 150 ms they sometimes did not).
        var segment = TimeSpan.FromSeconds(1);
        var instance = Instance(Limits(perAddress: 2, perUser: 0), segment: segment);
        var from = Address();

        Assert.True((await instance.DecideAsync(Request(from))).Allowed);
        Assert.True((await instance.DecideAsync(Request(from))).Allowed);
        var refused = await instance.DecideAsync(Request(from));
        Assert.False(refused.Allowed);
        Assert.InRange(refused.RetryAfter, TimeSpan.FromMilliseconds(1), segment);
        for (var i = 0; i < 20; i++) Assert.True((await instance.DecideAsync(Request(from, path: "/health"))).Allowed);

        await Task.Delay(segment * (SharedRateLimit.Segments + 1));
        Assert.True((await instance.DecideAsync(Request(from))).Allowed);
    }

    [RedisFact]
    public async Task With_no_limits_set_nothing_is_counted_and_redis_is_not_even_asked()
    {
        // Nothing listens here: if the limiter asked Redis, it would have to fall back.
        await using var nowhere = new RedisLink(new RedisOptions("redis://127.0.0.1:1"));
        var instance = Instance(Limits(0, 0), nowhere);

        for (var i = 0; i < 50; i++) Assert.True((await instance.DecideAsync(Request(Address(), "t"))).Allowed);
    }

    [RedisFact]
    public async Task Without_redis_each_instance_counts_on_its_own_rather_than_not_at_all()
    {
        await using var nowhere = new RedisLink(new RedisOptions("redis://127.0.0.1:1"));
        var instance = Instance(Limits(perAddress: 3, perUser: 0), nowhere);
        var from = Address();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 3; i++) Assert.True((await instance.DecideAsync(Request(from))).Allowed);
        var refused = await instance.DecideAsync(Request(from));

        Assert.False(refused.Allowed);
        Assert.True(refused.RetryAfter > TimeSpan.Zero);
        // And at once: a request does not wait for a Redis that is not there (it took about 5 s each before).
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"four decisions took {watch.Elapsed.TotalSeconds:0.0} s");
    }
}

/// <summary>Two API instances on one Redis: the limit a client meets is the same whichever instance it reaches.</summary>
[Collection("api")]
public sealed class SharedRateLimitApiTests(ApiHost host, RedisServer redis) : IClassFixture<RedisServer>
{
    [RedisFact]
    public async Task A_client_that_alternates_between_two_instances_is_limited_once_not_twice()
    {
        var before = Environment.GetEnvironmentVariable(ServiceLimits.PerUserVariable);
        Environment.SetEnvironmentVariable(ServiceLimits.PerUserVariable, "6");
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> first, second;
        try
        {
            first = host.StartVariant("CERBERUS_REDIS_URL", redis.Url);
            second = host.StartVariant("CERBERUS_REDIS_URL", redis.Url);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ServiceLimits.PerUserVariable, before);
        }
        using (first)
        using (second)
        {
            var onFirst = await ApiHost.AuthenticatedClientAsync(first);
            var onSecond = second.CreateClient();
            onSecond.DefaultRequestHeaders.Authorization = onFirst.DefaultRequestHeaders.Authorization;

            // Signing up was the first request without a token; from here on the token is counted.
            var codes = new List<HttpStatusCode>();
            for (var i = 0; i < 8; i++) codes.Add((await (i % 2 == 0 ? onFirst : onSecond).GetAsync("/jobs")).StatusCode);

            // Six allowed over both instances together — not six on each.
            Assert.Equal(6, codes.Count(c => c == HttpStatusCode.OK));
            Assert.Equal([HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes.Skip(6).ToArray());
            var refused = await onFirst.GetAsync("/jobs");
            Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
            Assert.True(int.Parse(refused.Headers.GetValues("Retry-After").Single()) >= 1);
            Assert.Equal("Too many requests. Try again shortly.", (await refused.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("error").GetString());

            // The liveness probe is never refused.
            Assert.Equal(HttpStatusCode.OK, (await onSecond.GetAsync("/health")).StatusCode);
        }
    }
}
