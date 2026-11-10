using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Cerberus.Authorization;

public enum ProofMethod
{
    /// <summary>A TXT record under the target's DNS name.</summary>
    Dns,

    /// <summary>A file served by the target at a well-known path.</summary>
    Http,
}

/// <summary>
/// The outside world as seen by ownership verification. Kept behind an
/// interface so the decision logic can be tested without network access.
/// </summary>
public interface IOwnershipProbe
{
    /// <summary>The TXT record values at <paramref name="name"/>; empty if there are none.</summary>
    Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken ct);

    /// <summary>The body served at <paramref name="url"/>, or null if it could not be fetched.</summary>
    Task<string?> FetchAsync(Uri url, CancellationToken ct);
}

/// <summary>What the operator must publish to prove control of a live target.</summary>
public sealed record OwnershipChallenge(
    string ExpectedValue,
    string? DnsRecordName,
    IReadOnlyList<Uri> HttpUrls);

public sealed record VerificationOutcome(bool Verified, ProofMethod? Method, string Detail);

/// <summary>
/// Proof-of-control for live targets (PLAN.md §3). The operator is given a
/// random token and must publish it somewhere only the target's owner can
/// write: a DNS TXT record, or a file on the host. Until that passes, the
/// target stays Pending and nothing above the passive tier may touch it.
/// </summary>
public static class OwnershipVerifier
{
    public const string DnsLabel = "_cerberus-challenge";
    public const string WellKnownPath = "/.well-known/cerberus-challenge.txt";
    private const string ValuePrefix = "cerberus-verify=";

    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    public static string ExpectedValue(string token) => ValuePrefix + token;

    public static OwnershipChallenge Describe(TargetType type, string locator, string token)
    {
        if (type == TargetType.Path)
        {
            throw new ArgumentException("Path targets are attested, not challenged.", nameof(type));
        }

        // DNS proof needs a name: an IP literal or a host:port has no record of its own.
        var dnsName = IsDnsName(locator) ? $"{DnsLabel}.{locator}" : null;
        Uri[] urls =
        [
            new($"https://{locator}{WellKnownPath}"),
            new($"http://{locator}{WellKnownPath}"),
        ];
        return new OwnershipChallenge(ExpectedValue(token), dnsName, urls);
    }

    /// <param name="method">Limit the attempt to one method; null tries DNS first, then HTTP.</param>
    public static async Task<VerificationOutcome> VerifyAsync(
        TargetType type, string locator, string token, ProofMethod? method,
        IOwnershipProbe probe, CancellationToken ct)
    {
        var challenge = Describe(type, locator, token);
        var attempts = new List<string>();

        if (method is null or ProofMethod.Dns)
        {
            if (challenge.DnsRecordName is null)
            {
                attempts.Add("DNS: not available for an IP address or a host with a port.");
            }
            else
            {
                var records = await probe.LookupTxtAsync(challenge.DnsRecordName, ct);
                if (records.Any(r => Matches(r, challenge.ExpectedValue)))
                {
                    return new VerificationOutcome(true, ProofMethod.Dns,
                        $"Found the challenge value in a TXT record at {challenge.DnsRecordName}.");
                }
                attempts.Add(records.Count == 0
                    ? $"DNS: no TXT record at {challenge.DnsRecordName}."
                    : $"DNS: {records.Count} TXT record(s) at {challenge.DnsRecordName}, none with the challenge value.");
            }
        }

        if (method is null or ProofMethod.Http)
        {
            foreach (var url in challenge.HttpUrls)
            {
                var body = await probe.FetchAsync(url, ct);
                if (body is not null && Matches(body, challenge.ExpectedValue))
                {
                    return new VerificationOutcome(true, ProofMethod.Http, $"Found the challenge value at {url}.");
                }
                attempts.Add(body is null
                    ? $"HTTP: could not fetch {url}."
                    : $"HTTP: {url} did not contain the challenge value.");
            }
        }

        return new VerificationOutcome(false, null, string.Join(" ", attempts));
    }

    /// <summary>Exact match after trimming whitespace and surrounding quotes, compared in fixed time.</summary>
    private static bool Matches(string candidate, string expected)
    {
        var a = Encoding.UTF8.GetBytes(candidate.Trim().Trim('"'));
        var b = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static bool IsDnsName(string locator) =>
        !locator.Contains(':') && !IPAddress.TryParse(locator, out _) && locator.Contains('.');
}
