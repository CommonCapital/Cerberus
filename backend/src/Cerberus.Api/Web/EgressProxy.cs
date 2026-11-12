using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cerberus.Api.Web;

/// <summary>
/// The only way out for the agents' browser. A browser makes requests of its
/// own — images, scripts, redirects, whatever a page's code decides to fetch —
/// so checking the address it is told to open is not enough. The browser is
/// started with this proxy as its only route, and the proxy applies the same
/// rule as <see cref="SafeFetcher"/> to every connection: the name is resolved
/// here, the address must be a public one, and the connection goes to exactly
/// that address. It only tunnels (HTTPS, port 443): a plain HTTP request is
/// refused, which also means the browser cannot be sent to an unencrypted page.
/// It listens on this machine only, for as long as one page visit lasts.
/// </summary>
public sealed class EgressProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly bool _allowLocal;
    private readonly ConcurrentBag<Task> _connections = [];

    /// <summary>Hosts the browser asked for and was not connected to, with the reason. For the run's record.</summary>
    public ConcurrentQueue<string> Refused { get; } = new();

    /// <summary>Hosts it was connected to.</summary>
    public ConcurrentQueue<string> Reached { get; } = new();

    public int Port { get; }

    /// <param name="allowLocal">Only for tests: lets connections be made to this machine, on any port.</param>
    public EgressProxy(bool allowLocal = false)
    {
        _allowLocal = allowLocal;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _connections.Add(ServeAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Stopped.
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var head = await ReadHeadAsync(stream, _stop.Token);
            if (head is null) return;

            var parts = head.Split('\n')[0].Trim().Split(' ');
            if (parts.Length < 2 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
            {
                Refused.Enqueue($"{(parts.Length > 1 ? HostOf(parts[1]) : "?")}: not an encrypted connection");
                await ReplyAsync(stream, "403 Forbidden");
                return;
            }

            var target = parts[1];
            var colon = target.LastIndexOf(':');
            var host = colon > 0 ? target[..colon].Trim('[', ']') : target;
            if (colon <= 0 || !int.TryParse(target[(colon + 1)..], out var port) || (!_allowLocal && port != 443))
            {
                Refused.Enqueue($"{host}: not the standard HTTPS port");
                await ReplyAsync(stream, "403 Forbidden");
                return;
            }

            IPAddress? address;
            try
            {
                var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, _stop.Token);
                address = addresses.FirstOrDefault(a => _allowLocal || PublicAddress.IsPublic(a));
            }
            catch (SocketException)
            {
                address = null;
            }
            if (address is null)
            {
                Refused.Enqueue($"{host}: not an address on the public internet");
                await ReplyAsync(stream, "403 Forbidden");
                return;
            }

            using var upstream = new TcpClient(address.AddressFamily) { NoDelay = true };
            try
            {
                using var connecting = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                connecting.CancelAfter(TimeSpan.FromSeconds(10));
                await upstream.ConnectAsync(address, port, connecting.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                Refused.Enqueue($"{host}: could not be reached");
                await ReplyAsync(stream, "502 Bad Gateway");
                return;
            }

            Reached.Enqueue(host);
            await ReplyAsync(stream, "200 Connection Established");
            var remote = upstream.GetStream();
            // A tunnel: bytes either way until one side is done. What is inside is the browser's TLS session.
            await Task.WhenAny(stream.CopyToAsync(remote, _stop.Token), remote.CopyToAsync(stream, _stop.Token));
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The browser or the site went away.
        }
    }

    private static string HostOf(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) ? uri.Host : target;

    /// <summary>The request head, up to the blank line. Null if it is not one (too long, or the connection ended).</summary>
    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            // One byte at a time near the end is not needed: nothing follows a CONNECT head until we answer.
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) return null;
            length += read;
            var text = Encoding.ASCII.GetString(buffer, 0, length);
            if (text.Contains("\r\n\r\n", StringComparison.Ordinal)) return text;
        }
        return null;
    }

    private static Task ReplyAsync(NetworkStream stream, string status) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\n\r\n")).AsTask();

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await Task.WhenAll(_connections).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or SocketException)
        {
            // Tunnels still closing; the listener is gone and the browser is about to be.
        }
        _stop.Dispose();
    }
}
