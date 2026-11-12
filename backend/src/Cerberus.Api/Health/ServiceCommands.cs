using System.Diagnostics;
using System.Text.Json;
using Cerberus.Api.Agents;
using Cerberus.Core.Models;
using Cerberus.Jobs;

namespace Cerberus.Api.Health;

/// <summary>
/// Things the server binary can do instead of serving (PLAN.md §21), so an
/// installation needs no extra tools:
/// <list type="bullet">
/// <item><c>health</c> — asks the service running on this machine whether it is
/// ready; the exit code is the answer. A container's healthcheck is this
/// command, so the image needs no curl.</item>
/// <item><c>smoke-llm &lt;model&gt;</c> — checks that a model can do what the
/// agents need of it: call a tool with valid arguments, and use the result.
/// The acceptance test for a newly configured local model or API key.</item>
/// <item><c>web-check</c>, <c>browser-check</c>, <c>mcp-check</c> — try what agents use to look things up
/// outside: fetch a page, open one in the browser, reach the configured MCP servers.</item>
/// </list>
/// </summary>
public static class ServiceCommands
{
    public const int Ok = 0;
    public const int Failed = 1;
    public const int Usage = 64;

    /// <summary>Exit code 0 when the service is ready (healthy or degraded), 1 when it is unhealthy or does not answer.</summary>
    public static async Task<int> HealthAsync(string baseUrl, TextWriter output, HttpClient http, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/health/ready", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            string status;
            try
            {
                using var json = JsonDocument.Parse(body);
                status = json.RootElement.GetProperty("status").GetString() ?? "unknown";
                output.WriteLine(status);
                foreach (var check in json.RootElement.GetProperty("checks").EnumerateArray())
                {
                    var state = check.GetProperty("status").GetString();
                    if (state != "healthy") output.WriteLine($"  {check.GetProperty("name").GetString()}: {state} — {check.GetProperty("detail").GetString()}");
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                output.WriteLine($"The service answered {(int)response.StatusCode} with something that is not a health report.");
                return Failed;
            }
            return response.IsSuccessStatusCode && status != "unhealthy" ? Ok : Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            output.WriteLine($"The service does not answer at {baseUrl}.");
            return Failed;
        }
    }

    private sealed class Silent : IRunReporter
    {
        public Task StepAsync(string id, string title, string status) => Task.CompletedTask;
        public Task ProgressAsync(int done, int total, string? message = null) => Task.CompletedTask;
        public Task FindingAsync(object finding) => Task.CompletedTask;
        public Task TextAsync(string channel, string delta) => Task.CompletedTask;
        public Task FlushTextAsync() => Task.CompletedTask;
        public Task EventAsync(string type, object data) => Task.CompletedTask;
        public Task<OperatorAnswer?> AskAsync(OperatorQuestionSpec question, CancellationToken ct) => Task.FromResult<OperatorAnswer?>(null);
    }

    /// <summary>A file that exists only for this test, holding a word the model cannot know without reading it.</summary>
    private sealed class OneFile(string word) : IAgentTool
    {
        public List<string> Asked { get; } = [];

        public ToolDefinition Definition { get; } = new(
            "readFile", "Read a text file. Returns its lines as `<line number>\\t<text>`.",
            JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string","description":"Path of the file to read"}},"required":["path"],"additionalProperties":false}""").RootElement.Clone());

        public Task<string> RunAsync(JsonElement input, CancellationToken ct)
        {
            if (!input.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String) throw new AgentToolException("path is required.");
            Asked.Add(path.GetString()!);
            return Task.FromResult($"1\tThe word is {word}.");
        }
    }

    /// <summary>
    /// `web-check &lt;url&gt;`: fetches one page the way an agent's researcher would — same rules, same refusals —
    /// and says what came back. For the operator who has switched web access on and wants to know it works,
    /// and that this server cannot be made to fetch from its own network.
    /// </summary>
    public static async Task<int> WebCheckAsync(Web.IWebFetcher fetcher, Web.WebOptions options, string? url, TextWriter output, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            output.WriteLine("Name the page to fetch: web-check <url>");
            return Usage;
        }
        output.WriteLine(options.Enabled
            ? "Web access for agents is ON (CERBERUS_WEB=on)."
            : "Web access for agents is OFF on this server (set CERBERUS_WEB=on to let agents research). The check below still runs.");
        output.WriteLine(string.IsNullOrWhiteSpace(options.SearchUrl)
            ? "No search service is configured (CERBERUS_SEARXNG_URL): a researcher can fetch pages it is pointed at, not search."
            : "A search service is configured.");
        if (new Web.EgressGuard(blocked: options.BlockedTerms).Refuse(url) is { } why)
        {
            output.WriteLine($"Refused before anything was sent: the address contains {why}.");
            return Failed;
        }
        try
        {
            var page = await fetcher.FetchAsync(url, ct);
            output.WriteLine($"Fetched {page.Url}: HTTP {page.Status}, {page.Text.Length} characters of text{(page.Truncated ? " (cut)" : "")}.");
            if (page.Title.Length > 0) output.WriteLine($"Title: {page.Title}");
            foreach (var line in page.Text.Split('\n').Take(3)) output.WriteLine($"  {(line.Length <= 120 ? line : line[..120] + "…")}");
            return Ok;
        }
        catch (Web.WebAccessException ex)
        {
            output.WriteLine($"Not fetched: {ex.Message}.");
            return Failed;
        }
    }

    /// <summary>
    /// `browser-check &lt;url&gt; [screens] [file.png]`: opens one page in the agents' browser — same rules, same
    /// refusals — scrolls, and says what it saw; with a file name it saves the screenshot there. For the operator
    /// who has switched the browser on and wants to see that it works and what it will not open.
    /// </summary>
    public static async Task<int> BrowserCheckAsync(
        Web.IAgentBrowser browser, Web.BrowserOptions options, string? url, int scroll, string? saveTo, TextWriter output, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            output.WriteLine("Name the page to open: browser-check <https url> [screens to scroll] [file.png]");
            return Usage;
        }
        output.WriteLine(options.Enabled
            ? "The browser is ON for agents (CERBERUS_BROWSER=on)."
            : "The browser is OFF for agents on this server (set CERBERUS_BROWSER=on, and CERBERUS_WEB=on). The check below still runs.");
        if (new Web.EgressGuard().Refuse(url) is { } why)
        {
            output.WriteLine($"Refused before anything was opened: the address contains {why}.");
            return Failed;
        }
        try
        {
            var page = await browser.OpenAsync(url, scroll, saveTo is not null, ct);
            output.WriteLine($"Opened {page.Url}: \"{page.Title}\", {page.Position}, {page.Text.Length} characters of text{(page.Truncated ? " (cut)" : "")}.");
            foreach (var line in page.Text.Split('\n').Where(l => l.Trim().Length > 0).Take(3)) output.WriteLine($"  {(line.Length <= 120 ? line : line[..120] + "…")}");
            foreach (var refused in page.Refused) output.WriteLine($"Not allowed out: {refused}");
            if (saveTo is not null && page.Screenshot is { } png)
            {
                await File.WriteAllBytesAsync(saveTo, png, ct);
                output.WriteLine($"Screenshot saved to {Path.GetFullPath(saveTo)} ({png.Length / 1024} KB).");
            }
            return Ok;
        }
        catch (Web.WebAccessException ex)
        {
            output.WriteLine($"Not opened: {ex.Message}.");
            return Failed;
        }
    }

    /// <summary>
    /// `mcp-check [server [tool [json-arguments]]]`: reaches the configured MCP servers and shows what an agent
    /// would be shown of each — which tools, and why the others are withheld. With a tool named, calls it once,
    /// through the same check of the arguments an agent's call goes through.
    /// </summary>
    public static async Task<int> McpCheckAsync(
        Web.IMcpServers servers, Web.WebOptions web, string? only, string? tool, string? arguments, TextWriter output, CancellationToken ct)
    {
        foreach (var problem in servers.Problems) output.WriteLine($"Configuration: {problem}.");
        if (servers.Names.Count == 0)
        {
            output.WriteLine("No MCP servers are configured. Name them in a JSON file and point CERBERUS_MCP_CONFIG at it:");
            output.WriteLine("""  { "servers": [ { "name": "docs", "url": "https://example.org/mcp", "headers": { "Authorization": "env:DOCS_MCP_TOKEN" }, "tools": ["search"] } ] }""");
            return servers.Problems.Count > 0 ? Failed : Usage;
        }
        if (only is not null && !servers.Names.Contains(only, StringComparer.OrdinalIgnoreCase))
        {
            output.WriteLine($"No server named \"{only}\" is configured. Configured: {string.Join(", ", servers.Names)}");
            return Usage;
        }

        var states = (await servers.StatesAsync(refresh: true, ct)).Where(s => only is null || s.Name.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var state in states)
        {
            if (!state.Reachable)
            {
                output.WriteLine($"{state.Name} ({state.Address}): NOT AVAILABLE — {state.Problem}{(state.Detail is { Length: > 0 } said ? $" (the server said: {said})" : "")}.");
                continue;
            }
            output.WriteLine($"{state.Name} ({state.Address}): ok — \"{state.ServerName}\", protocol {state.Protocol}, " +
                             $"{state.Exposed.Count} tool(s) for agents, {state.Withheld.Count} withheld.");
            foreach (var exposed in state.Exposed) output.WriteLine($"  + {exposed.ModelName}  (the server's {exposed.Tool})");
            foreach (var withheld in state.Withheld) output.WriteLine($"  - {withheld.Tool}: {withheld.Why}");
        }
        if (tool is null) return states.All(s => s.Reachable) ? Ok : Failed;

        var target = states.Single();
        if (!target.Reachable) return Failed;
        if (target.Exposed.FirstOrDefault(t => t.Tool == tool || t.ModelName == tool) is not { } chosen)
        {
            output.WriteLine($"\"{tool}\" is not a tool agents may use on {target.Name}.");
            return Usage;
        }
        JsonElement input;
        try
        {
            input = JsonDocument.Parse(string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments).RootElement.Clone();
        }
        catch (JsonException)
        {
            output.WriteLine("The arguments are not valid JSON. Example: mcp-check docs search '{\"query\":\"rate limiting\"}'");
            return Usage;
        }
        if (input.ValueKind != JsonValueKind.Object)
        {
            output.WriteLine("The arguments must be a JSON object.");
            return Usage;
        }
        if (Web.McpAgentTool.Refuse(input, new Web.EgressGuard(blocked: web.BlockedTerms)) is { } why)
        {
            output.WriteLine($"Refused before anything was sent: an argument contains {why}.");
            return Failed;
        }
        try
        {
            var result = await servers.CallAsync(target.Name, chosen.Tool, input, ct);
            output.WriteLine($"Called {chosen.Tool}: {(result.IsError ? "the tool reported a failure" : "ok")}, {result.Text.Length} characters.");
            foreach (var line in result.Text.Split('\n').Where(l => l.Trim().Length > 0).Take(5)) output.WriteLine($"  {(line.Length <= 160 ? line : line[..160] + "…")}");
            return result.IsError ? Failed : Ok;
        }
        catch (Web.McpException ex)
        {
            output.WriteLine($"No result: {ex.Message}{(ex.Detail is { Length: > 0 } said ? $" (the server said: {said})" : "")}.");
            return Failed;
        }
    }

    public const string SmokeTask = "read the file smoke.txt and reply with the word it contains";

    /// <summary>
    /// One tool-call round trip through the real provider stack and the real agent loop.
    /// Keys come from the environment (ANTHROPIC_API_KEY, OPENAI_API_KEY); a local model needs none.
    /// </summary>
    public static async Task<int> SmokeAsync(AgentLoop loop, string? model, ModelCredentials credentials, TextWriter output, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model) || !ModelRegistry.IsSupported(model))
        {
            output.WriteLine(string.IsNullOrWhiteSpace(model) ? "Name the model to test: smoke-llm <model>" : $"\"{model}\" is not a model this server knows.");
            output.WriteLine($"Known models: {string.Join(", ", ModelRegistry.Models.Select(m => m.Id))}");
            return Usage;
        }

        var word = $"CERBERUS-{Guid.NewGuid():n}"[..21].ToUpperInvariant();
        var file = new OneFile(word);
        var watch = Stopwatch.StartNew();
        AgentOutcome outcome;
        try
        {
            outcome = await loop.RunAsync(
                model, credentials, "You are being tested. Use the tool you are given; do not guess.", SmokeTask, [file], maxSteps: 4, new Silent(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            output.WriteLine($"{model}: FAILED — the model could not be called: {ex.Message}");
            return Failed;
        }
        watch.Stop();

        var called = file.Asked.Count > 0;
        var used = outcome.FinalText.Contains(word, StringComparison.OrdinalIgnoreCase);
        output.WriteLine($"{model}: {(called && used ? "ok" : "FAILED")}");
        output.WriteLine($"  called the tool with valid arguments: {(called ? $"yes (path \"{file.Asked[0]}\")" : "no")}");
        output.WriteLine($"  used the tool's result in its answer: {(used ? "yes" : "no")}");
        output.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"  steps {outcome.Steps}, tool calls {outcome.ToolCalls}, {watch.Elapsed.TotalSeconds:0.0} s") +
                         (outcome.InputTokens is { } i ? $", tokens in {i} out {outcome.OutputTokens ?? 0}" : ""));
        if (!called) output.WriteLine("  The agents cannot work with a model that does not call tools. For a local model, check that it supports tool calling.");
        else if (!used) output.WriteLine("  The model called the tool but did not answer from what it returned.");
        return called && used ? Ok : Failed;
    }
}
