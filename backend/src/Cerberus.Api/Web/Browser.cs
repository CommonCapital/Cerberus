using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cerberus.Api.Web;

/// <param name="Enabled">Whether the researcher may use a browser (CERBERUS_BROWSER=on). Needs web access switched on as well.</param>
/// <param name="Path">The Chrome or Chromium binary (CERBERUS_BROWSER_PATH); looked for in the usual places when not given.</param>
/// <param name="NoSandbox">Start the browser without its own sandbox (CERBERUS_BROWSER_NO_SANDBOX=true): needed when running as root in a container, and weaker.</param>
public sealed record BrowserOptions(bool Enabled = false, string? Path = null, bool NoSandbox = false);

/// <param name="Text">The page's visible text as rendered, after its scripts have run.</param>
/// <param name="Screenshot">A PNG of what was on screen at the end, or null when none was asked for.</param>
/// <param name="Position">How far down the page the view ended, e.g. "screen 3 of 7".</param>
/// <param name="Refused">Connections the page tried to make that were not allowed (private addresses, unencrypted).</param>
public sealed record BrowsedPage(
    string Url, string Title, string Text, bool Truncated, byte[]? Screenshot, string Position, IReadOnlyList<string> Refused);

public interface IAgentBrowser
{
    /// <param name="scroll">How many screens to scroll down before reading and taking the picture (0–10).</param>
    Task<BrowsedPage> OpenAsync(string url, int scroll, bool screenshot, CancellationToken ct);
}

/// <summary>
/// A real browser for the researcher: it opens a page, lets the page's scripts
/// run, scrolls, reads what is on it and takes a picture. One headless Chrome
/// per visit, with an empty profile that is deleted afterwards — nothing is
/// remembered from one page to the next, and nothing of the operator's own
/// browser (cookies, sign-ins, history) is ever involved. Its only route out
/// is the <see cref="EgressProxy"/>, so neither the page nor anything the page
/// loads can reach a private network. Downloads are refused.
/// It is driven over the DevTools protocol on a local port that exists for the
/// length of the visit.
/// </summary>
public sealed partial class ChromeBrowser(BrowserOptions options, WebOptions web) : IAgentBrowser
{
    public const int MaxScroll = 10;
    public const int MaxText = 20_000;
    public const int Width = 1280;
    public const int Height = 800;
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan LoadLimit = TimeSpan.FromSeconds(20);

    private static readonly string[] UsualPlaces =
    [
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        "/Applications/Chromium.app/Contents/MacOS/Chromium",
        "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable",
    ];

    /// <summary>The binary that would be used, or null when there is none.</summary>
    public string? Binary() =>
        options.Path is { Length: > 0 } given ? (File.Exists(given) ? given : null) : UsualPlaces.FirstOrDefault(File.Exists);

    /// <summary>What an address must be to be opened. Throws saying why not.</summary>
    public Uri Vet(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https")
        {
            throw new WebAccessException("the browser opens https pages only");
        }
        if (uri.UserInfo.Length > 0) throw new WebAccessException("an address with a user name or password in it is not opened");
        if (!web.AllowLocalAddresses && !uri.IsDefaultPort) throw new WebAccessException("only the standard HTTPS port is opened");
        if (System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var literal) && !web.AllowLocalAddresses && !PublicAddress.IsPublic(literal))
        {
            throw new WebAccessException("that is an address inside a private network, which is not opened");
        }
        return uri;
    }

    public async Task<BrowsedPage> OpenAsync(string url, int scroll, bool screenshot, CancellationToken ct)
    {
        var uri = Vet(url);
        var binary = Binary() ?? throw new WebAccessException("no browser is installed on this server");
        scroll = Math.Clamp(scroll, 0, MaxScroll);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Limit);
        var profile = Path.Combine(Path.GetTempPath(), $"cerberus-browser-{Guid.NewGuid():n}");
        Directory.CreateDirectory(profile);
        await using var proxy = new EgressProxy(web.AllowLocalAddresses);
        Process? chrome = null;
        try
        {
            chrome = Start(binary, profile, proxy.Port);
            var endpoint = await DevToolsEndpointAsync(chrome, limit.Token);
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(endpoint), limit.Token);
            await using var devtools = new DevTools(socket);

            // Nothing is to be saved to this machine, whatever a page offers.
            await devtools.SendAsync("Browser.setDownloadBehavior", new JsonObject { ["behavior"] = "deny" }, null, limit.Token);
            var target = await devtools.SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" }, null, limit.Token);
            var attached = await devtools.SendAsync("Target.attachToTarget",
                new JsonObject { ["targetId"] = target.GetProperty("targetId").GetString(), ["flatten"] = true }, null, limit.Token);
            var session = attached.GetProperty("sessionId").GetString()!;

            await devtools.SendAsync("Page.enable", null, session, limit.Token);
            await devtools.SendAsync("Emulation.setDeviceMetricsOverride",
                new JsonObject { ["width"] = Width, ["height"] = Height, ["deviceScaleFactor"] = 1, ["mobile"] = false }, session, limit.Token);

            // Pages are shown the same way whatever this machine's own settings are: a picture is a record.
            await devtools.SendAsync("Emulation.setEmulatedMedia",
                new JsonObject { ["features"] = new JsonArray(new JsonObject { ["name"] = "prefers-color-scheme", ["value"] = "light" }) }, session, limit.Token);

            var loaded = devtools.WaitForAsync("Page.loadEventFired", session);
            var navigated = await devtools.SendAsync("Page.navigate", new JsonObject { ["url"] = uri.ToString() }, session, limit.Token);
            if (navigated.TryGetProperty("errorText", out var error) && error.GetString() is { Length: > 0 } reason)
            {
                throw new WebAccessException(reason.Contains("TUNNEL", StringComparison.OrdinalIgnoreCase) || reason.Contains("PROXY", StringComparison.OrdinalIgnoreCase)
                    ? "that page is not on the public internet, or could not be reached"
                    : $"the page could not be opened ({reason.Replace("net::", "")})");
            }
            // A page that never finishes loading is read as far as it got.
            await Task.WhenAny(loaded, Task.Delay(LoadLimit, limit.Token));
            await Task.Delay(700, limit.Token);

            for (var i = 0; i < scroll; i++)
            {
                await EvaluateAsync(devtools, session, "window.scrollBy(0, window.innerHeight); 0", limit.Token);
                await Task.Delay(350, limit.Token);
            }

            var state = JsonDocument.Parse(await EvaluateAsync(devtools, session,
                "JSON.stringify({ title: document.title, url: location.href, text: document.body ? document.body.innerText : '', " +
                "y: Math.round(window.scrollY), h: document.documentElement.scrollHeight, vh: window.innerHeight })", limit.Token)).RootElement;
            var text = Blank().Replace(state.GetProperty("text").GetString() ?? "", "\n\n").Trim();
            var height = Math.Max(1, state.GetProperty("vh").GetInt32());
            var screens = Math.Max(1, (int)Math.Ceiling(state.GetProperty("h").GetDouble() / height));
            var at = Math.Min(screens, state.GetProperty("y").GetInt32() / height + 1);

            byte[]? picture = null;
            if (screenshot)
            {
                var shot = await devtools.SendAsync("Page.captureScreenshot", new JsonObject { ["format"] = "png" }, session, limit.Token);
                picture = Convert.FromBase64String(shot.GetProperty("data").GetString()!);
            }

            return new BrowsedPage(
                state.GetProperty("url").GetString() ?? uri.ToString(), state.GetProperty("title").GetString() ?? "",
                text.Length <= MaxText ? text : text[..MaxText], text.Length > MaxText, picture, $"screen {at} of {screens}",
                proxy.Refused.Distinct().Take(10).ToList());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WebAccessException($"the page did not open within {Limit.TotalSeconds:0} seconds");
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            throw new WebAccessException("the browser stopped before the page could be read");
        }
        finally
        {
            Stop(chrome);
            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The browser may still be letting go of a file; the directory is in the temp folder either way.
            }
        }
    }

    private Process Start(string binary, string profile, int proxyPort)
    {
        var start = new ProcessStartInfo(binary) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        string[] arguments =
        [
            "--headless=new", "--remote-debugging-port=0", $"--user-data-dir={profile}",
            // The only way out, for every address — this machine's own included, so that it is refused like any other private one.
            $"--proxy-server=http://127.0.0.1:{proxyPort}", "--proxy-bypass-list=<-loopback>",
            // Nothing that goes around a proxy.
            "--disable-quic", "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
            // Nothing the browser does on its own account.
            "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync", "--disable-background-networking",
            "--disable-component-update", "--disable-default-apps", "--disable-client-side-phishing-detection",
            "--disable-features=Translate,MediaRouter,OptimizationHints,AutofillServerCommunication,InterestFeedContentSuggestions",
            "--mute-audio", "--hide-scrollbars", $"--window-size={Width},{Height}", "about:blank",
        ];
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (options.NoSandbox) start.ArgumentList.Add("--no-sandbox");
        return Process.Start(start) ?? throw new WebAccessException("the browser could not be started");
    }

    /// <summary>Chrome says on its error stream where it listens for DevTools; that line is waited for.</summary>
    private static async Task<string> DevToolsEndpointAsync(Process chrome, CancellationToken ct)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        startup.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (await chrome.StandardError.ReadLineAsync(startup.Token) is { } line)
            {
                var match = Listening().Match(line);
                if (match.Success)
                {
                    // Keep draining what the browser prints, so it never blocks on a full pipe.
                    _ = chrome.StandardError.ReadToEndAsync(CancellationToken.None);
                    _ = chrome.StandardOutput.ReadToEndAsync(CancellationToken.None);
                    return match.Groups[1].Value;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Falls through to the error below.
        }
        throw new WebAccessException("the browser did not start");
    }

    private static async Task<string> EvaluateAsync(DevTools devtools, string session, string expression, CancellationToken ct)
    {
        var result = await devtools.SendAsync("Runtime.evaluate",
            new JsonObject { ["expression"] = expression, ["returnByValue"] = true }, session, ct);
        return result.TryGetProperty("result", out var value) && value.TryGetProperty("value", out var actual)
            ? (actual.ValueKind == JsonValueKind.String ? actual.GetString()! : actual.GetRawText())
            : "";
    }

    private static void Stop(Process? chrome)
    {
        if (chrome is null) return;
        try
        {
            if (!chrome.HasExited) chrome.Kill(entireProcessTree: true);
            chrome.WaitForExit(3000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
        finally
        {
            chrome.Dispose();
        }
    }

    [GeneratedRegex(@"DevTools listening on (ws://\S+)")]
    private static partial Regex Listening();

    [GeneratedRegex(@"\n\s*\n\s*")]
    private static partial Regex Blank();

    /// <summary>The DevTools protocol over one socket: commands answered by id, and events that can be waited for.</summary>
    private sealed class DevTools : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _events = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _reading;
        private int _nextId;

        public DevTools(ClientWebSocket socket)
        {
            _socket = socket;
            _reading = ReadAsync();
        }

        public async Task<JsonElement> SendAsync(string method, JsonObject? parameters, string? session, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId);
            var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = answer;
            var message = new JsonObject { ["id"] = id, ["method"] = method };
            if (parameters is not null) message["params"] = parameters;
            if (session is not null) message["sessionId"] = session;
            await _socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, ct);
            using var registration = ct.Register(() => answer.TrySetCanceled(ct));
            return await answer.Task;
        }

        /// <summary>Completes when the event next arrives for this session.</summary>
        public Task WaitForAsync(string method, string session) =>
            _events.GetOrAdd($"{session}:{method}", _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        private async Task ReadAsync()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult part;
                    do
                    {
                        part = await _socket.ReceiveAsync(buffer, _stop.Token);
                        if (part.MessageType == WebSocketMessageType.Close) return;
                        message.Write(buffer, 0, part.Count);
                    }
                    while (!part.EndOfMessage);

                    using var json = JsonDocument.Parse(message.ToArray());
                    var root = json.RootElement;
                    if (root.TryGetProperty("id", out var id) && _pending.TryRemove(id.GetInt32(), out var answer))
                    {
                        if (root.TryGetProperty("error", out var error))
                        {
                            answer.TrySetException(new InvalidOperationException(error.TryGetProperty("message", out var m) ? m.GetString() : "DevTools error"));
                        }
                        else
                        {
                            answer.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
                        }
                    }
                    else if (root.TryGetProperty("method", out var method))
                    {
                        var session = root.TryGetProperty("sessionId", out var s) ? s.GetString() : "";
                        if (_events.TryRemove($"{session}:{method.GetString()}", out var waiter)) waiter.TrySetResult(true);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or JsonException)
            {
                // The browser is gone; whoever is still waiting is told below.
            }
            finally
            {
                foreach (var waiting in _pending.Values) waiting.TrySetException(new IOException("The browser closed the connection."));
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            try
            {
                await _reading.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Still reading; the socket is disposed by its owner.
            }
            _stop.Dispose();
        }
    }
}
