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
