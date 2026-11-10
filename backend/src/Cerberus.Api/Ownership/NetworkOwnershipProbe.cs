using System.Net;
using System.Net.Sockets;
using Cerberus.Authorization;
using DnsClient;

namespace Cerberus.Api.Ownership;

/// <summary>Settings for the ownership probe.</summary>
/// <param name="AllowLocalAddresses">
/// Permit fetching from loopback and link-local addresses. Off by default so a
/// verification request cannot be pointed at this machine or at a cloud
/// metadata endpoint (169.254.169.254).
/// </param>
public sealed record OwnershipProbeOptions(bool AllowLocalAddresses);

/// <summary>
/// Real-network implementation of <see cref="IOwnershipProbe"/>. Read-only and
/// deliberately narrow: one TXT lookup, or one GET of a fixed well-known path
/// with no redirects, a short timeout and a small response cap.
/// </summary>
public sealed class NetworkOwnershipProbe : IOwnershipProbe, IDisposable
{
    private const int MaxBodyBytes = 4096;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly LookupClient _dns = new(new LookupClientOptions { Timeout = Timeout, Retries = 1, UseCache = false });
    private readonly HttpClient _http;

    public NetworkOwnershipProbe(OwnershipProbeOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,   // a redirect could leave the target the operator named
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = Timeout,
            // Resolve and vet the address ourselves, then connect to exactly that
            // address — so the check cannot be bypassed by a second DNS answer.
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var address = addresses.FirstOrDefault(a => options.AllowLocalAddresses || !IsLocal(a))
                    ?? throw new HttpRequestException(
                        $"{context.DnsEndPoint.Host} resolves only to loopback or link-local addresses, which are not fetched.");

                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _http = new HttpClient(handler) { Timeout = Timeout };
    }

    public async Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken ct)
    {
        try
        {
            var response = await _dns.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
            return response.Answers.TxtRecords().Select(r => string.Concat(r.Text)).ToList();
        }
        catch (Exception ex) when (ex is DnsResponseException or SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    public async Task<string?> FetchAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxBodyBytes];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0)
            {
                total += read;
            }
            return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal) return true;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;

        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 169 && bytes[1] == 254;
    }

    public void Dispose() => _http.Dispose();
}
