using System.Net;
using Cerberus.Api.Ownership;
using Cerberus.Authorization;

namespace Cerberus.Tests;

public class OwnershipVerifierTests
{
    private const string Token = "0123abcd";
    private const string Value = "cerberus-verify=0123abcd";

    private sealed class Probe : IOwnershipProbe
    {
        public Dictionary<string, string[]> Txt { get; } = new();
        public Dictionary<string, string?> Pages { get; } = new();
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken ct)
        {
            Calls.Add($"dns:{name}");
            return Task.FromResult<IReadOnlyList<string>>(Txt.GetValueOrDefault(name, []));
        }

        public Task<string?> FetchAsync(Uri url, CancellationToken ct)
        {
            Calls.Add($"http:{url}");
            return Task.FromResult(Pages.GetValueOrDefault(url.ToString()));
        }
    }

    private static Task<VerificationOutcome> Verify(
        Probe probe, TargetType type = TargetType.Domain, string locator = "example.com", ProofMethod? method = null) =>
        OwnershipVerifier.VerifyAsync(type, locator, Token, method, probe, CancellationToken.None);

    [Fact]
    public void Challenge_names_the_record_and_the_well_known_urls()
    {
        var challenge = OwnershipVerifier.Describe(TargetType.Domain, "example.com", Token);

        Assert.Equal(Value, challenge.ExpectedValue);
        Assert.Equal("_cerberus-challenge.example.com", challenge.DnsRecordName);
        Assert.Equal(
            ["https://example.com/.well-known/cerberus-challenge.txt", "http://example.com/.well-known/cerberus-challenge.txt"],
            challenge.HttpUrls.Select(u => u.ToString()).ToArray());
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("web-01.internal:8443")]
    [InlineData("localhost")]
    public void Hosts_without_a_dns_name_of_their_own_only_get_the_http_challenge(string host)
    {
        Assert.Null(OwnershipVerifier.Describe(TargetType.Host, host, Token).DnsRecordName);
    }

    [Fact]
    public void Tokens_are_random_and_long()
    {
        var a = OwnershipVerifier.NewToken();

        Assert.Equal(64, a.Length);
        Assert.NotEqual(a, OwnershipVerifier.NewToken());
    }

    [Fact]
    public async Task Txt_record_with_the_value_verifies_without_touching_http()
    {
        var probe = new Probe { Txt = { ["_cerberus-challenge.example.com"] = ["v=spf1 -all", $"\"{Value}\""] } };

        var outcome = await Verify(probe);

        Assert.True(outcome.Verified);
        Assert.Equal(ProofMethod.Dns, outcome.Method);
        Assert.DoesNotContain(probe.Calls, c => c.StartsWith("http:"));
    }

    [Fact]
    public async Task Well_known_file_verifies_when_dns_has_nothing()
    {
        var probe = new Probe { Pages = { ["http://example.com/.well-known/cerberus-challenge.txt"] = Value + "\n" } };

        var outcome = await Verify(probe);

        Assert.True(outcome.Verified);
        Assert.Equal(ProofMethod.Http, outcome.Method);
    }

    [Theory]
    [InlineData("cerberus-verify=0123abcd-and-more")]
    [InlineData("prefix cerberus-verify=0123abcd")]
    [InlineData("cerberus-verify=0123ABCD")]
    [InlineData("0123abcd")]
    [InlineData("")]
    public async Task Anything_but_the_exact_value_is_refused(string published)
    {
        var probe = new Probe
        {
            Txt = { ["_cerberus-challenge.example.com"] = [published] },
            Pages = { ["https://example.com/.well-known/cerberus-challenge.txt"] = published },
        };

        var outcome = await Verify(probe);

        Assert.False(outcome.Verified);
        Assert.Null(outcome.Method);
    }

    [Fact]
    public async Task A_record_on_the_bare_domain_does_not_count()
    {
        // The value must sit under the challenge label, not just anywhere in the zone.
        var probe = new Probe { Txt = { ["example.com"] = [Value] } };

        Assert.False((await Verify(probe)).Verified);
    }

    [Fact]
    public async Task Restricting_the_method_skips_the_other_one()
    {
        var probe = new Probe { Txt = { ["_cerberus-challenge.example.com"] = [Value] } };

        var outcome = await Verify(probe, method: ProofMethod.Http);

        Assert.False(outcome.Verified);
        Assert.DoesNotContain(probe.Calls, c => c.StartsWith("dns:"));
    }

    [Fact]
    public async Task Failure_explains_every_attempt()
    {
        var outcome = await Verify(new Probe());

        Assert.Contains("no TXT record at _cerberus-challenge.example.com", outcome.Detail);
        Assert.Contains("could not fetch https://example.com/", outcome.Detail);
        Assert.Contains("could not fetch http://example.com/", outcome.Detail);
    }

    [Fact]
    public async Task Path_targets_cannot_be_challenged()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Verify(new Probe(), TargetType.Path, "app"));
    }
}

public class OwnershipScopeTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private static ScopeDecision Check(VerificationStatus verification, CapabilityTier tier) =>
        ScopePolicy.Check(
            new EngagementScope(EngagementStatus.Active, CapabilityTier.T1, null, null,
                [new ScopedTarget(TargetType.Domain, "example.com", verification)]),
            tier, TargetType.Domain, "api.example.com", Now);

    [Fact]
    public void An_unverified_live_target_allows_passive_work_only()
    {
        Assert.True(Check(VerificationStatus.Pending, CapabilityTier.T0).Allowed);

        var active = Check(VerificationStatus.Pending, CapabilityTier.T1);
        Assert.False(active.Allowed);
        Assert.Contains("proof-of-control", active.Reason);
    }

    [Fact]
    public void A_verified_live_target_allows_active_safe_work()
    {
        Assert.True(Check(VerificationStatus.Verified, CapabilityTier.T1).Allowed);
    }
}

public class NetworkOwnershipProbeTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("169.254.169.254", true)]     // cloud metadata endpoint
    [InlineData("fe80::1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("::ffff:127.0.0.1", true)]    // IPv4-mapped loopback
    [InlineData("10.1.2.3", false)]           // private ranges are legitimate internal targets
    [InlineData("192.168.1.10", false)]
    [InlineData("93.184.216.34", false)]
    public void Loopback_and_link_local_addresses_are_never_fetched_by_default(string address, bool local)
    {
        Assert.Equal(local, NetworkOwnershipProbe.IsLocal(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task A_listening_loopback_server_is_fetched_only_when_local_addresses_are_allowed()
    {
        // A real server on this machine that would happily answer.
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var served = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref served);
                    var stream = client.GetStream();
                    var buffer = new byte[2048];
                    _ = await stream.ReadAsync(buffer, stop.Token);
                    var body = "cerberus-verify=abc";
                    var response = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(response), stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        });

        var urls = new[]
        {
            new Uri($"http://127.0.0.1:{port}/.well-known/cerberus-challenge.txt"),
            new Uri($"http://localhost:{port}/.well-known/cerberus-challenge.txt"),
        };

        using (var guarded = new NetworkOwnershipProbe(new OwnershipProbeOptions(AllowLocalAddresses: false)))
        {
            foreach (var url in urls) Assert.Null(await guarded.FetchAsync(url, CancellationToken.None));
            Assert.Equal(0, served);   // never even connected
        }

        using (var permissive = new NetworkOwnershipProbe(new OwnershipProbeOptions(AllowLocalAddresses: true)))
        {
            Assert.Equal("cerberus-verify=abc", await permissive.FetchAsync(urls[0], CancellationToken.None));
        }

        stop.Cancel();
        await server;
    }
}
