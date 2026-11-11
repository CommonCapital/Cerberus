using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerberus.Core.Models;

namespace Cerberus.Models;

/// <summary>
/// Dev/test provider behind the "echo" model id: deterministic, no network, no
/// API key, and not a language model. It exercises the whole chat pipeline —
/// including tool calls — by following simple rules on the last user message:
/// <list type="bullet">
/// <item>"read &lt;path&gt;" → calls the client tool <c>readFile</c>;</item>
/// <item>"list &lt;path&gt;" → calls the client tool <c>listDirectory</c>;</item>
/// <item>"skills about &lt;words&gt;" → calls the server tool <c>searchSkills</c>;</item>
/// <item>once the tools have answered, it replies with what they returned;</item>
/// <item>anything else is echoed back.</item>
/// </list>
/// </summary>
public sealed partial class MockProvider : IModelProvider
{
    public ModelProvider Provider => ModelProvider.Mock;

    public async IAsyncEnumerable<string> StreamAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lastUser = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        await foreach (var word in Words($"Echo: {lastUser}".Trim(), cancellationToken))
        {
            yield return word;
        }
    }

    public async IAsyncEnumerable<ChatEvent> StreamChatAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var lastUserIndex = request.Turns.ToList().FindLastIndex(t => t.Role == "user");
        var lastUser = lastUserIndex >= 0 ? ChatEvents.TextOf(request.Turns[lastUserIndex]) : "";
        var results = request.Turns.Skip(lastUserIndex + 1)
            .Where(t => t.Role == "tool")
            .SelectMany(t => t.Parts.OfType<ToolResultPart>())
            .ToList();

        // Offered the review agent's tools: act out a small review by fixed rules.
        if (request.Tools.Any(t => t.Name == "reportFinding"))
        {
            var (tool, input, closing) = NextReviewAction(request);
            if (tool is not null)
            {
                await foreach (var word in Words($"Calling {tool}.", cancellationToken)) yield return new TextDelta(word);
                foreach (var e in ToolCall(tool, input!)) yield return e;
                yield return new UsageReport(InputTokens: 12, OutputTokens: 8);
                yield return new StepFinished("tool_calls");
            }
            else
            {
                await foreach (var word in Words(closing!, cancellationToken)) yield return new TextDelta(word);
                yield return new UsageReport(InputTokens: 20, OutputTokens: 10);
                yield return new StepFinished("stop");
            }
            yield break;
        }

        // After the tools have answered: report what they said, and stop.
        if (results.Count > 0)
        {
            var summary = string.Join(" | ", results.Select(r =>
                $"{r.Name} {(r.IsError ? "failed" : "returned")}: {Shorten(r.Content)}"));
            await foreach (var word in Words(summary, cancellationToken)) yield return new TextDelta(word);
            yield return new UsageReport(InputTokens: 20, OutputTokens: 10);
            yield return new StepFinished("stop");
            yield break;
        }

        (string Tool, object Input)? call = lastUser switch
        {
            _ when ReadPattern().Match(lastUser) is { Success: true } m => ("readFile", new { path = m.Groups[1].Value }),
            _ when ListPattern().Match(lastUser) is { Success: true } m => ("listDirectory", new { path = m.Groups[1].Value }),
            _ when SkillsPattern().Match(lastUser) is { Success: true } m => ("searchSkills", new { query = m.Groups[1].Value.Trim(), limit = 3 }),
            _ => null,
        };

        if (call is { } c && request.Tools.Any(t => t.Name == c.Tool))
        {
            await foreach (var word in Words($"Calling {c.Tool}.", cancellationToken)) yield return new TextDelta(word);

            foreach (var e in ToolCall(c.Tool, c.Input)) yield return e;
            yield return new UsageReport(InputTokens: 12, OutputTokens: 8);
            yield return new StepFinished("tool_calls");
            yield break;
        }

        await foreach (var word in Words($"Echo: {lastUser}".Trim(), cancellationToken)) yield return new TextDelta(word);
        yield return new UsageReport(InputTokens: 5, OutputTokens: 5);
        yield return new StepFinished("stop");
    }

    private static IEnumerable<ChatEvent> ToolCall(string tool, object input)
    {
        var id = $"call_{Guid.NewGuid():n}";
        var json = JsonSerializer.Serialize(input);
        yield return new ToolCallStart(id, tool);
        yield return new ToolCallInputDelta(id, json[..(json.Length / 2)]);
        yield return new ToolCallInputDelta(id, json[(json.Length / 2)..]);
        yield return new ToolCallComplete(id, tool, json);
    }

    /// <summary>
    /// The review agent, acted out without a model: list the top directory,
    /// read its first few files, report the first line of each that calls one
    /// of a few well-known risky functions, then sum up. Enough to exercise
    /// the agent pipeline end to end; not a review.
    /// </summary>
    private static (string? Tool, object? Input, string? Closing) NextReviewAction(ChatRequest request)
    {
        const int maxFiles = 5;
        var calls = request.Turns.SelectMany(t => t.Parts.OfType<ToolCallPart>()).ToList();
        var results = request.Turns.SelectMany(t => t.Parts.OfType<ToolResultPart>()).ToList();
        if (calls.Count == 0) return ("listDirectory", new { path = "." }, null);

        string? PathOf(ToolCallPart call, string property) =>
            call.Input.ValueKind == JsonValueKind.Object && call.Input.TryGetProperty(property, out var v) ? v.GetString() : null;

        var read = calls.Where(c => c.Name == "readFile").Select(c => PathOf(c, "path")).ToList();
        var reported = calls.Where(c => c.Name == "reportFinding").Select(c => PathOf(c, "file")).ToList();

        // The file just read: report its first risky line, once.
        if (results.Count > 0 && results[^1] is { Name: "readFile", IsError: false } last &&
            calls.FirstOrDefault(c => c.Id == last.ToolCallId) is { } readCall && PathOf(readCall, "path") is { } file &&
            !reported.Contains(file) && RiskyLine().Match(last.Content) is { Success: true } risky)
        {
            return ("reportFinding", new
            {
                title = $"Call to {risky.Groups[2].Value.TrimEnd('(')} (flagged by the dev model)",
                severity = "Medium",
                category = "other",
                file,
                line = int.Parse(risky.Groups[1].Value),
                description = "The dev model flags this call by a fixed rule. It is not a language model and has not reviewed the code.",
                remediation = "Have a person or a real model review this call.",
            }, null);
        }

        // Otherwise the next unread file of the listing, if any.
        var listing = results.FirstOrDefault(r => r is { Name: "listDirectory", IsError: false });
        if (listing is not null && read.Count < maxFiles)
        {
            try
            {
                using var doc = JsonDocument.Parse(listing.Content);
                var next = doc.RootElement.GetProperty("entries").EnumerateArray()
                    .Where(e => e.GetProperty("type").GetString() == "file")
                    .Select(e => e.GetProperty("name").GetString())
                    .FirstOrDefault(name => name is not null && !read.Contains(name));
                if (next is not null) return ("readFile", new { path = next }, null);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Not the listing this script expects: stop here.
            }
        }

        return (null, null,
            $"Read {read.Count} file(s) and reported {reported.Count} finding(s). This was the dev model following fixed rules, not a review.");
    }

    [GeneratedRegex(@"^(\d+)\t.*?\b(pickle\.loads\(|eval\(|md5\(|os\.system\()", RegexOptions.Multiline)]
    private static partial Regex RiskyLine();

    private static async IAsyncEnumerable<string> Words(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            yield return word + " ";
            await Task.Delay(2, ct);
        }
    }

    private static string Shorten(string value) => value.Length <= 160 ? value : value[..160] + "…";

    [GeneratedRegex(@"^read (?:the file )?(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReadPattern();

    [GeneratedRegex(@"^list (\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ListPattern();

    [GeneratedRegex(@"^skills about (.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex SkillsPattern();
}
