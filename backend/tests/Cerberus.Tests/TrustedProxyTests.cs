using System.Net;
using Cerberus.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Cerberus.Tests;

/// <summary>Whose word about a client's address is taken: only a proxy the installation named.</summary>
public class TrustedProxiesTests
{
    private static TrustedProxies Proxies(string? setting) => TrustedProxies.FromEnvironment(_ => setting);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("10.0.0.5")]
    [InlineData("172.20.0.0/16")]
    [InlineData("10.0.0.5, 172.20.0.0/16 ,fd00::/8, ::1")]
    public void An_address_or_a_network_is_a_valid_entry(string? setting)
    {
        Assert.Null(TrustedProxies.Problem(setting));
    }

    [Theory]
    [InlineData("proxy")]                 // a name is not an address: it could come to mean anything
    [InlineData("10.0.0.5/40")]
    [InlineData("10.0.0.0/8, nonsense")]
    [InlineData("0.0.0.0/0")]             // everyone: the header could then be written by anyone
    [InlineData("::/0")]
    public void What_cannot_be_trusted_safely_is_refused_and_named(string setting)
    {
        var problem = TrustedProxies.Problem(setting);
        Assert.NotNull(problem);
        Assert.StartsWith(TrustedProxies.Variable, problem);
        Assert.Single(ServiceConfig.Validate(name => name == TrustedProxies.Variable ? setting : null));
    }

    [Fact]
    public void Nothing_set_trusts_nobody()
    {
        Assert.False(Proxies(null).Any);
        Assert.Null(Proxies(null).ClientBehind(IPAddress.Parse("10.0.0.5"), "1.2.3.4"));
        Assert.False(TrustedProxies.None.Any);
    }

    [Fact]
    public void A_trusted_proxy_is_believed_about_the_address_it_saw()
    {
        var proxies = Proxies("10.0.0.0/8, 192.168.1.7");

        Assert.Equal(IPAddress.Parse("203.0.113.9"), proxies.ClientBehind(IPAddress.Parse("10.2.3.4"), "203.0.113.9"));
        Assert.Equal(IPAddress.Parse("203.0.113.9"), proxies.ClientBehind(IPAddress.Parse("192.168.1.7"), "203.0.113.9"));
        // As a container sees an IPv4 peer on a dual-stack socket.
        Assert.Equal(IPAddress.Parse("203.0.113.9"), proxies.ClientBehind(IPAddress.Parse("::ffff:10.2.3.4"), "203.0.113.9"));
        Assert.Equal(IPAddress.Parse("2001:db8::7"), proxies.ClientBehind(IPAddress.Parse("10.2.3.4"), "2001:db8::7"));
    }

    [Fact]
    public void Only_the_entry_the_proxy_itself_added_counts()
    {
        // A client may send the header too; the proxy appends what it saw. The last entry is the proxy's.
        var proxies = Proxies("10.0.0.0/8");
        Assert.Equal(IPAddress.Parse("203.0.113.9"), proxies.ClientBehind(IPAddress.Parse("10.2.3.4"), "1.1.1.1, 8.8.8.8 , 203.0.113.9"));
    }

    [Theory]
    [InlineData("192.168.1.8", "203.0.113.9")]    // not a trusted proxy: the header is anyone's claim
    [InlineData("11.0.0.1", "203.0.113.9")]
    [InlineData("10.2.3.4", null)]                // trusted, but it named nobody
    [InlineData("10.2.3.4", "")]
    [InlineData("10.2.3.4", "not-an-address")]
    [InlineData("10.2.3.4", "203.0.113.9, unknown")]
    public void Otherwise_the_connections_own_address_stands(string remote, string? header)
    {
        Assert.Null(Proxies("10.0.0.0/8, 192.168.1.7").ClientBehind(IPAddress.Parse(remote), header));
    }

    [Fact]
    public void A_connection_without_an_address_is_never_a_trusted_proxy()
    {
        Assert.Null(Proxies("10.0.0.0/8").ClientBehind(null, "203.0.113.9"));
    }
}

/// <summary>The same through a running API: the request-rate limit counts the client, not the proxy.</summary>
[Collection("api")]
public class TrustedProxyApiTests(ApiHost host)
{
    /// <summary>Gives each test request the network address it names, as if it had arrived from there.</summary>
    private sealed class ArrivesFrom : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, following) =>
            {
                if (IPAddress.TryParse(context.Request.Headers["X-Test-Arrives-From"], out var address))
                    context.Connection.RemoteIpAddress = address;
                return following(context);
            });
            next(app);
        };
    }

    /// <summary>
    /// A second API instance with these settings and the filter above. Settings are read when a host starts,
    /// so they stay in place until this one has started, and are then put back as they were.
    /// </summary>
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Start(params (string Name, string Value)[] settings)
    {
        var before = settings.Select(s => (s.Name, Value: Environment.GetEnvironmentVariable(s.Name))).ToArray();
        foreach (var (name, value) in settings) Environment.SetEnvironmentVariable(name, value);
        try
        {
            var factory = host.StartVariant(settings[0].Name, settings[0].Value)
                .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter, ArrivesFrom>()));
            foreach (var (name, value) in settings) Environment.SetEnvironmentVariable(name, value);   // StartVariant put its own back
            factory.CreateClient().Dispose();   // starts it now
            return factory;
        }
        finally
        {
            foreach (var (name, value) in before) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static async Task<HttpStatusCode> Get(HttpClient client, string arrivesFrom, string? forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/sessions");
        request.Headers.Add("X-Test-Arrives-From", arrivesFrom);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_each_client_has_its_own_allowance_and_a_forged_header_gains_nothing()
    {
        {
            using var limited = Start(("CERBERUS_RATE_PER_ADDRESS", "3"), (TrustedProxies.Variable, "10.0.0.0/8"));
            using var client = limited.CreateClient();
            const string proxy = "10.0.0.5";

            // One client behind the proxy uses up its allowance…
            for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Unauthorized, await Get(client, proxy, "203.0.113.1"));
            Assert.Equal(HttpStatusCode.TooManyRequests, await Get(client, proxy, "203.0.113.1"));
            // …and another, through the same proxy, still has theirs.
            Assert.Equal(HttpStatusCode.Unauthorized, await Get(client, proxy, "203.0.113.2"));
            // Writing other addresses in front of the proxy's entry does not make a new client.
            Assert.Equal(HttpStatusCode.TooManyRequests, await Get(client, proxy, "198.51.100.77, 203.0.113.1"));

            // From an address that is not a trusted proxy the header is ignored: one allowance, whatever it claims.
            const string stranger = "198.51.100.9";
            for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Unauthorized, await Get(client, stranger, $"203.0.113.{50 + i}"));
            Assert.Equal(HttpStatusCode.TooManyRequests, await Get(client, stranger, "203.0.113.99"));
        }
    }

    [Fact]
    public async Task With_no_proxy_named_the_header_is_ignored_from_everyone()
    {
        using var limited = Start(("CERBERUS_RATE_PER_ADDRESS", "3"));
        using var client = limited.CreateClient();

        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.Unauthorized, await Get(client, "10.0.0.5", $"203.0.113.{i}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Get(client, "10.0.0.5", "203.0.113.200"));
    }
}
