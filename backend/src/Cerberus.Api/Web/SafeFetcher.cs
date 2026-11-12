using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cerberus.Api.Web;

/// <param name="Enabled">Whether agents may use the internet at all. Off unless the installation turns it on (CERBERUS_WEB=on).</param>
/// <param name="SearchUrl">A SearXNG instance to search with (CERBERUS_SEARXNG_URL); without one there is fetching but no searching.</param>
/// <param name="AllowLocalAddresses">Only for tests: lets pages be fetched from this machine.</param>
/// <param name="BlockedTerms">Terms that never leave this installation (CERBERUS_EGRESS_BLOCKED_TERMS).</param>
public sealed record WebOptions(bool Enabled = false, string? SearchUrl = null, bool AllowLocalAddresses = false, IReadOnlyList<string>? BlockedTerms = null);

public sealed record FetchedPage(string Url, int Status, string Title, string Text, bool Truncated);

/// <summary>A page could not be fetched, for a reason the model can be told.</summary>
public sealed class WebAccessException(string message) : Exception(message);

public interface IWebFetcher
{
    Task<FetchedPage> FetchAsync(string url, CancellationToken ct);
}

/// <summary>
/// Fetches a web page for an agent without becoming a way into this network
/// (server-side request forgery). The rules:
/// <list type="bullet">
/// <item>http and https only, on their standard ports; no credentials in the address;</item>
/// <item>the address a name resolves to is checked <i>at the moment of connecting</i>, and the connection goes
/// to exactly that address — so a name cannot resolve to something public for the check and something
/// private for the request;</item>
/// <item>redirects are followed by hand, a few at most, each through the same check;</item>
/// <item>no cookies, no credentials, nothing of the caller but a fixed agent name;</item>
/// <item>a time limit, a size limit, and text only — the page comes back as plain text.</item>
/// </list>
/// </summary>
public sealed partial class SafeFetcher : IWebFetcher, IDisposable
{
    public const int MaxBytes = 2_000_000;
    public const int MaxText = 20_000;
    public const int MaxRedirects = 5;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly WebOptions _options;
    private readonly HttpClient _http;

    public SafeFetcher(WebOptions options)
    {
        _options = options;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // The check that matters: every connection, whatever name it was asked for, goes to an address vetted here.
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                var address = addresses.FirstOrDefault(a => options.AllowLocalAddresses || PublicAddress.IsPublic(a))
                    ?? throw new WebAccessException(addresses.Length == 0
                        ? "that name does not resolve to any address"
                        : "that name leads to an address inside a private network, which is not fetched");
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
        _http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>What an address must be to be fetched at all. Returns the parsed address, or throws saying why not.</summary>
    public Uri Vet(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new WebAccessException("only http and https addresses are fetched");
        }
        if (uri.UserInfo.Length > 0) throw new WebAccessException("an address with a user name or password in it is not fetched");
        if (!_options.AllowLocalAddresses && !uri.IsDefaultPort) throw new WebAccessException("only the standard web ports are fetched");
        // A literal private address needs no lookup to be refused.
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && !_options.AllowLocalAddresses && !PublicAddress.IsPublic(literal))
        {
            throw new WebAccessException("that is an address inside a private network, which is not fetched");
        }
        return uri;
    }

    public async Task<FetchedPage> FetchAsync(string url, CancellationToken ct)
    {
        var uri = Vet(url);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Cerberus-research/1.0");
                request.Headers.Accept.ParseAdd("text/html, text/plain, application/json;q=0.9, application/xhtml+xml;q=0.9");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);

                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects) throw new WebAccessException($"more than {MaxRedirects} redirects");
                    uri = Vet(new Uri(uri, location).ToString());
                    continue;
                }

                var type = response.Content.Headers.ContentType?.MediaType ?? "";
                var isText = type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                             type is "application/json" or "application/xhtml+xml" or "application/xml" or "";
                if (!isText) throw new WebAccessException($"that is not a page of text (it is {type})");

                var (bytes, cut) = await ReadLimitedAsync(response, limit.Token);
                var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
                var raw = Decode(bytes, charset);
                var html = type.Contains("html", StringComparison.OrdinalIgnoreCase) || (type == "" && raw.Contains("<html", StringComparison.OrdinalIgnoreCase));
                var text = html ? TextOf(raw) : raw.Trim();
                var title = html ? TitleOf(raw) : "";
                var truncated = cut || text.Length > MaxText;
                return new FetchedPage(uri.ToString(), (int)response.StatusCode, title, truncated ? text[..Math.Min(text.Length, MaxText)] : text, truncated);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WebAccessException($"the page did not arrive within {Timeout.TotalSeconds:0} seconds");
        }
        catch (HttpRequestException ex)
        {
            // The refusal of the connect check arrives wrapped; its message is the one worth passing on.
            throw new WebAccessException(ex.InnerException is WebAccessException inner ? inner.Message : "the page could not be reached");
        }
    }

    private static async Task<(byte[] Bytes, bool Cut)> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            var room = MaxBytes - (int)buffer.Length;
            buffer.Write(chunk, 0, Math.Min(read, room));
            if (read >= room) return (buffer.ToArray(), true);
        }
        return (buffer.ToArray(), false);
    }

    private static string Decode(byte[] bytes, string? charset)
    {
        try
        {
            return (charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset)).GetString(bytes);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>The words of a page: no scripts, styles or markup; one line per block.</summary>
    public static string TextOf(string html)
    {
        var text = Invisible().Replace(html, " ");
        text = Comments().Replace(text, " ");
        text = BlockEnd().Replace(text, "\n");
        text = Tag().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        var lines = text.Split('\n').Select(line => Spaces().Replace(line, " ").Trim()).Where(line => line.Length > 0);
        return string.Join("\n", lines);
    }

    public static string TitleOf(string html)
    {
        var match = Title().Match(html);
        return match.Success ? Spaces().Replace(WebUtility.HtmlDecode(match.Groups[1].Value), " ").Trim() : "";
    }

    public void Dispose() => _http.Dispose();

    [GeneratedRegex(@"<(script|style|noscript|svg|template|head)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Invisible();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"</(p|div|li|tr|h[1-6]|section|article|pre|blockquote|table|ul|ol)\s*>|<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r\f\v ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Title();
}

public sealed record SearchHit(string Title, string Url, string Snippet);

public interface IWebSearch
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken ct);
}

/// <summary>
/// Search through a SearXNG instance the installation runs itself (its JSON API). Self-hosted on purpose:
/// what agents search for says a great deal about what is being reviewed.
/// </summary>
public sealed class SearxngSearch(HttpClient http, WebOptions options) : IWebSearch
{
    public const int MaxHits = 8;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.SearchUrl)) throw new WebAccessException("no search service is configured on this server");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await http.GetAsync(
                $"{options.SearchUrl.TrimEnd('/')}/search?format=json&safesearch=1&q={Uri.EscapeDataString(query)}", limit.Token);
            if (!response.IsSuccessStatusCode) throw new WebAccessException($"the search service answered {(int)response.StatusCode}");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token));
            if (!body.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return [];
            return results.EnumerateArray()
                .Select(r => new SearchHit(Text(r, "title"), Text(r, "url"), Text(r, "content")))
                .Where(hit => hit.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                .Take(MaxHits)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new WebAccessException("the search service could not be reached");
        }
    }

    private static string Text(JsonElement node, string name)
    {
        var value = node.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String ? found.GetString()! : "";
        return value.Length <= 500 ? value : value[..500];
    }
}
