using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cerberus.Api.Web;

/// <summary>One MCP server the installation has configured.</summary>
/// <param name="Name">The short name agents and operators see it under.</param>
/// <param name="Headers">Sent with every request — where a token goes. Never shown.</param>
/// <param name="Tools">The tools agents may use, by the server's own names. Null: only the tools the server marks read-only.</param>
public sealed record McpServerConfig(string Name, Uri Url, IReadOnlyDictionary<string, string> Headers, IReadOnlyList<string>? Tools)
{
    /// <summary>Where it is, for showing: no query string, which is where some servers take a key.</summary>
    public string Address => Url.GetLeftPart(UriPartial.Path);
}

/// <summary>
/// The MCP servers of this installation (PLAN.md §21). They are named in a file the administrator writes
/// (<c>CERBERUS_MCP_CONFIG</c>), not chosen by a model or sent in a request: which outside services an agent
/// can reach is a decision about the installation. An entry that cannot be used is left out and said why.
/// </summary>
/// <param name="Problems">What was wrong with the file or with single entries, for the operator.</param>
/// <param name="AllowPlainHttpAnywhere">Only for tests.</param>
public sealed partial record McpOptions(
    IReadOnlyList<McpServerConfig> Servers, IReadOnlyList<string> Problems, bool AllowPlainHttpAnywhere = false)
{
    public const int MaxServers = 8;

    public static McpOptions None { get; } = new([], []);

    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Type", "Content-Length", "Accept", "Connection", "Transfer-Encoding", McpClient.SessionHeader, McpClient.VersionHeader,
    };

    /// <summary>Reads the file. A missing or broken file configures nothing and says so; it never stops the server.</summary>
    /// <param name="env">Looks up an environment variable, for header values written as <c>env:NAME</c>.</param>
    public static McpOptions Load(string? path, Func<string, string?> env)
    {
        if (string.IsNullOrWhiteSpace(path)) return None;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new([], ["the MCP configuration file could not be read"]);
        }
        return Parse(text, env);
    }

    public static McpOptions Parse(string json, Func<string, string?> env)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return new([], ["the MCP configuration file is not valid JSON"]);
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("servers", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return new([], ["the MCP configuration file needs a \"servers\" list"]);
            }

            var servers = new List<McpServerConfig>();
            var problems = new List<string>();
            var position = 0;
            foreach (var entry in list.EnumerateArray())
            {
                position++;
                if (servers.Count >= MaxServers)
                {
                    problems.Add($"more than {MaxServers} servers: the rest are not used");
                    break;
                }
                var name = entry.ValueKind == JsonValueKind.Object ? Text(entry, "name") : null;
                if (name is null || !ServerName().IsMatch(name))
                {
                    problems.Add($"server {position}: needs a \"name\" of letters, digits and dashes, at most 24 characters");
                    continue;
                }
                if (servers.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    problems.Add($"server \"{name}\": the name is used twice; the second is not used");
                    continue;
                }
                if (Server(entry, name, env, out var problem) is { } server) servers.Add(server);
                else problems.Add($"server \"{name}\": {problem}");
            }
            return new(servers, problems);
        }
    }

    private static McpServerConfig? Server(JsonElement entry, string name, Func<string, string?> env, out string problem)
    {
        problem = "";
        if (!Uri.TryCreate(Text(entry, "url"), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            problem = "needs a \"url\" starting with https:// (or http:// for a server on this machine or network)";
            return null;
        }
        if (url.UserInfo.Length > 0 || url.Fragment.Length > 0)
        {
            problem = "the url must not carry a user name, a password or a fragment; put a token in \"headers\"";
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (entry.TryGetProperty("headers", out var given) && given.ValueKind != JsonValueKind.Null)
        {
            if (given.ValueKind != JsonValueKind.Object)
            {
                problem = "\"headers\" must be an object of names and values";
                return null;
            }
            foreach (var header in given.EnumerateObject())
            {
                if (!HeaderName().IsMatch(header.Name) || ReservedHeaders.Contains(header.Name))
                {
                    problem = $"the header \"{(header.Name.Length <= 40 ? header.Name : header.Name[..40])}\" cannot be set";
                    return null;
                }
                var value = header.Value.ValueKind == JsonValueKind.String ? header.Value.GetString()! : "";
                if (value.StartsWith("env:", StringComparison.Ordinal))
                {
                    var variable = value[4..].Trim();
                    value = env(variable) ?? "";
                    if (value.Length == 0)
                    {
                        problem = $"the environment variable {variable} (for the header {header.Name}) is not set";
                        return null;
                    }
                }
                if (value.Length == 0 || value.Any(c => c is '\r' or '\n' || char.IsControl(c)))
                {
                    problem = $"the header {header.Name} has no usable value";
                    return null;
                }
                headers[header.Name] = value;
            }
        }

        List<string>? tools = null;
        if (entry.TryGetProperty("tools", out var named) && named.ValueKind != JsonValueKind.Null)
        {
            if (named.ValueKind != JsonValueKind.Array || named.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String))
            {
                problem = "\"tools\" must be a list of tool names";
                return null;
            }
            tools = named.EnumerateArray().Select(t => t.GetString()!.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        }
        return new McpServerConfig(name, url, headers, tools);
    }

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String ? found.GetString()!.Trim() : null;

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9\-]{0,23}\z")]
    private static partial Regex ServerName();

    [GeneratedRegex(@"\A[A-Za-z0-9!#$%&'*+.^_`|~\-]{1,64}\z")]
    private static partial Regex HeaderName();
}

/// <summary>A server could not be used.</summary>
/// <param name="message">In this system's own words: safe to pass to a model.</param>
/// <param name="detail">What the server itself said, if anything — for the operator only, never for a model.</param>
public sealed class McpException(string message, string? detail = null) : Exception(message)
{
    public string? Detail { get; } = detail;
}

/// <param name="ReadOnly">The server's own claim that the tool changes nothing (its <c>readOnlyHint</c>); null when it does not say.</param>
public sealed record McpToolInfo(string Name, string Description, JsonElement InputSchema, bool? ReadOnly, bool? Destructive);

public sealed record McpServerInfo(string Name, string Version, string Protocol, bool OffersTools);

/// <param name="Text">What the tool returned, as text. Pictures, sound and files are named, not carried.</param>
/// <param name="IsError">The tool ran and reported a failure of its own.</param>
public sealed record McpCallResult(string Text, bool IsError);

/// <summary>
/// A client for one MCP server over the Streamable HTTP transport (protocol revision 2025-06-18), written
/// against the specification and nothing else: JSON-RPC messages are POSTed one at a time, and a reply comes
/// either as one JSON object or as a stream of server-sent events that ends with it. The client offers the
/// server no capabilities of its own — no sampling, no file roots, no questions to the user — so the only
/// thing a server can do is answer.
/// <para>
/// What it will not do: follow a redirect (the token in the headers would go with it), speak plain http to
/// an address on the public internet, keep cookies, wait without limit, or read without limit.
/// </para>
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";
    public static readonly IReadOnlyList<string> Supported = ["2025-06-18", "2025-03-26"];
    public const string SessionHeader = "Mcp-Session-Id";
    public const string VersionHeader = "MCP-Protocol-Version";
    public const int MaxResponseBytes = 2_000_000;
    public const int MaxTools = 200;
    public const int MaxPages = 10;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly McpServerConfig _server;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _nextId;
    private string? _session;
    private McpServerInfo? _info;

    public McpClient(McpServerConfig server, McpOptions options, TimeSpan? timeout = null)
    {
        _server = server;
        _timeout = timeout ?? DefaultTimeout;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, ct) =>
            {
                var secure = context.InitialRequestMessage.RequestUri?.Scheme == "https";
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                // Plain http is for a server on this machine or network; over the internet a token would travel in the clear.
                var usable = addresses.Where(a => MayConnect(secure, a, options.AllowPlainHttpAnywhere)).ToList();
                if (usable.Count == 0)
                {
                    throw new McpException(addresses.Length == 0
                        ? "its name does not resolve to any address"
                        : "it is on the public internet and its address starts with http://; such a server needs https://");
                }
                Exception? last = null;
                foreach (var address in usable)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (SocketException ex)
                    {
                        socket.Dispose();
                        last = ex;
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
                throw last!;
            },
        };
        _http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>Whether a connection of this kind may go to this address.</summary>
    public static bool MayConnect(bool https, IPAddress address, bool allowPlainHttpAnywhere = false) =>
        https || allowPlainHttpAnywhere || !PublicAddress.IsPublic(address);

    /// <summary>Introduces the client to the server, once. Safe to call again.</summary>
    public async Task<McpServerInfo> ConnectAsync(CancellationToken ct)
    {
        if (_info is { } known) return known;
        await _gate.WaitAsync(ct);
        try
        {
            return _info ??= await InitializeAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        var info = await ConnectAsync(ct);
        if (!info.OffersTools) return [];

        var tools = new List<McpToolInfo>();
        string? cursor = null;
        for (var page = 0; page < MaxPages && tools.Count < MaxTools; page++)
        {
            var result = await RequestAsync("tools/list", cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor }, ct);
            if (result.TryGetProperty("tools", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var tool in list.EnumerateArray())
                {
                    if (Tool(tool) is { } read && tools.Count < MaxTools) tools.Add(read);
                }
            }
            cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return tools;
    }

    public async Task<McpCallResult> CallToolAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        var parameters = new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments.ValueKind == JsonValueKind.Object ? JsonNode.Parse(arguments.GetRawText()) : new JsonObject(),
        };
        return Render(await RequestAsync("tools/call", parameters, ct));
    }

    /// <summary>The text of a tool's result. What is not text is named and left behind.</summary>
    public static McpCallResult Render(JsonElement result)
    {
        var text = new StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var type = String(item, "type");
                if (text.Length > 0) text.Append('\n');
                switch (type)
                {
                    case "text":
                        text.Append(String(item, "text"));
                        break;
                    case "resource" when item.TryGetProperty("resource", out var resource) && resource.ValueKind == JsonValueKind.Object:
                        text.Append(String(resource, "text") is { Length: > 0 } body
                            ? $"[document {String(resource, "uri")}]\n{body}"
                            : $"[a file was returned and is not shown: {String(resource, "uri")}]");
                        break;
                    case "resource_link":
                        text.Append($"[link: {String(item, "uri")} {String(item, "name")}]");
                        break;
                    case "image" or "audio":
                        text.Append($"[{(type == "image" ? "a picture" : "sound")} was returned and is not shown]");
                        break;
                    default:
                        text.Append("[something that is not text was returned and is not shown]");
                        break;
                }
            }
        }
        // A server may answer with data only; then the data is the text.
        if (text.Length == 0 && result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object)
        {
            text.Append(structured.GetRawText());
        }
        var failed = result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True;
        return new McpCallResult(text.Length > 0 ? text.ToString() : "(the tool returned nothing)", failed);
    }

    private static McpToolInfo? Tool(JsonElement tool)
    {
        if (tool.ValueKind != JsonValueKind.Object) return null;
        var name = String(tool, "name");
        if (name.Length is 0 or > 128) return null;
        var schema = tool.TryGetProperty("inputSchema", out var given) && given.ValueKind == JsonValueKind.Object ? given.Clone() : default;
        bool? readOnly = null, destructive = null;
        if (tool.TryGetProperty("annotations", out var notes) && notes.ValueKind == JsonValueKind.Object)
        {
            readOnly = Flag(notes, "readOnlyHint");
            destructive = Flag(notes, "destructiveHint");
        }
        return new McpToolInfo(name, String(tool, "description"), schema, readOnly, destructive);
    }

    private static bool? Flag(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static string String(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    private async Task<McpServerInfo> InitializeAsync(CancellationToken ct)
    {
        _session = null;
        var result = await ExchangeAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            // Nothing is offered: the server cannot ask this client to run a model, list files or question the user.
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "cerberus", ["version"] = "1.0" },
        }, negotiated: null, ct);

        var version = String(result, "protocolVersion");
        if (!Supported.Contains(version))
        {
            throw new McpException("it speaks a version of the protocol this client does not", $"protocol version \"{Short(version)}\"");
        }
        var serverInfo = result.TryGetProperty("serverInfo", out var given) && given.ValueKind == JsonValueKind.Object ? given : default;
        var info = new McpServerInfo(
            serverInfo.ValueKind == JsonValueKind.Object ? Short(String(serverInfo, "name")) : "",
            serverInfo.ValueKind == JsonValueKind.Object ? Short(String(serverInfo, "version")) : "",
            version,
            result.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object && capabilities.TryGetProperty("tools", out _));

        // The server's "instructions" are deliberately not read: text from outside does not get to instruct an agent.
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, id: null, version, ct);
        return info;
    }

    /// <summary>One request and its reply. A session the server has forgotten is started again, once.</summary>
    private async Task<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
    {
        var info = await ConnectAsync(ct);
        try
        {
            return await ExchangeAsync(method, parameters, info.Protocol, ct);
        }
        catch (SessionGone)
        {
            await _gate.WaitAsync(ct);
            try
            {
                _info = null;
                info = _info = await InitializeAsync(ct);
            }
            finally
            {
                _gate.Release();
            }
            try
            {
                return await ExchangeAsync(method, (JsonObject)parameters.DeepClone(), info.Protocol, ct);
            }
            catch (SessionGone)
            {
                throw new McpException("it keeps ending the session");
            }
        }
    }

    private async Task<JsonElement> ExchangeAsync(string method, JsonObject parameters, string? negotiated, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
        var reply = await SendAsync(message, id, negotiated, ct)
            ?? throw new McpException("it did not answer the request");
        if (reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var number) ? number : 0;
            throw new McpException($"it answered with an error (code {code})", Short(String(error, "message"), 300));
        }
        if (!reply.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            throw new McpException("its answer was not in the form the protocol asks for");
        }
        return result.Clone();
    }

    /// <summary>POSTs one message. For a request (<paramref name="id"/> set) returns the reply to it; for a notification, null.</summary>
    private async Task<JsonElement?> SendAsync(JsonObject message, long? id, string? negotiated, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _server.Url);
            request.Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.UserAgent.ParseAdd("Cerberus-mcp/1.0");
            foreach (var (name, value) in _server.Headers) request.Headers.TryAddWithoutValidation(name, value);
            var session = _session;
            if (session is not null) request.Headers.TryAddWithoutValidation(SessionHeader, session);
            if (negotiated is not null) request.Headers.TryAddWithoutValidation(VersionHeader, negotiated);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            var status = (int)response.StatusCode;
            if (status == 404 && session is not null) throw new SessionGone();
            if (status is >= 300 and < 400) throw new McpException("it answered with a redirect, which is not followed; configure the address it finally lives at");
            if (status is 401 or 403) throw new McpException($"it refused the request (HTTP {status}); check the token in its headers");
            if (status is < 200 or >= 300) throw new McpException($"it answered HTTP {status}");

            if (message["method"]?.GetValue<string>() == "initialize" &&
                response.Headers.TryGetValues(SessionHeader, out var assigned) && assigned.FirstOrDefault() is { } given)
            {
                // Visible ASCII only, as the specification requires; anything else is not sent back.
                _session = given.Length is > 0 and <= 512 && given.All(c => c is >= '\x21' and <= '\x7e')
                    ? given
                    : throw new McpException("it assigned a session identifier that is not valid");
            }
            if (id is null) return null;

            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            if (type.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase)) return await ReadEventsAsync(stream, id.Value, limit.Token);
            if (!type.Equals("application/json", StringComparison.OrdinalIgnoreCase))
            {
                throw new McpException("it did not answer with JSON or an event stream; is this the server's MCP address?");
            }
            return Reply(await ReadLimitedAsync(stream, limit.Token), id.Value)
                ?? throw new McpException("its answer was not a reply to the request");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new McpException($"it did not answer within {_timeout.TotalSeconds:0} seconds");
        }
        catch (HttpRequestException ex)
        {
            throw new McpException(ex.InnerException is McpException inner ? inner.Message : "it could not be reached");
        }
        catch (IOException)
        {
            throw new McpException("the connection to it broke off");
        }
    }

    /// <summary>
    /// Reads server-sent events until the reply to request <paramref name="id"/> arrives. Anything else the server
    /// sends first — progress, log lines, requests of its own — is passed over: this client offered it nothing to ask for.
    /// </summary>
    private static async Task<JsonElement?> ReadEventsAsync(Stream stream, long id, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        var read = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            read += line.Length + 1;
            if (read > MaxResponseBytes) throw new McpException("it sent more than this client reads");
            if (line.Length == 0)
            {
                if (data.Length > 0 && Reply(data.ToString(), id) is { } reply) return reply;
                data.Clear();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5));
            }
        }
        // A stream that ends without a blank line after its last event.
        return data.Length > 0 ? Reply(data.ToString(), id) : throw new McpException("it closed the stream without answering");
    }

    /// <summary>The reply to request <paramref name="id"/> in a JSON text — one message, or (older servers) a list of them.</summary>
    private static JsonElement? Reply(string json, long id)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new McpException("it sent something that is not JSON");
        }
        using (document)
        {
            var root = document.RootElement;
            foreach (var message in root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : [root])
            {
                if (message.ValueKind != JsonValueKind.Object) continue;
                if (!message.TryGetProperty("id", out var given) || given.ValueKind != JsonValueKind.Number || !given.TryGetInt64(out var number) || number != id) continue;
                if (message.TryGetProperty("result", out _) || message.TryGetProperty("error", out _)) return message.Clone();
            }
            return null;
        }
    }

    private static async Task<string> ReadLimitedAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) throw new McpException("it sent more than this client reads");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Short(string text, int max = 80)
    {
        var clean = Untrusted.Clean(text).ReplaceLineEndings(" ").Trim();
        return clean.Length <= max ? clean : clean[..max];
    }

    /// <summary>Tells the server the session is over, if there is one; it may not care.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_session is { } session)
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var request = new HttpRequestMessage(HttpMethod.Delete, _server.Url);
                foreach (var (name, value) in _server.Headers) request.Headers.TryAddWithoutValidation(name, value);
                request.Headers.TryAddWithoutValidation(SessionHeader, session);
                if (_info is { } info) request.Headers.TryAddWithoutValidation(VersionHeader, info.Protocol);
                using var response = await _http.SendAsync(request, limit.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or McpException or IOException)
            {
                // Ending a session is a courtesy.
            }
        }
        _http.Dispose();
        _gate.Dispose();
    }

    private sealed class SessionGone : Exception;
}
