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

            var id = $"call_{Guid.NewGuid():n}";
            var json = JsonSerializer.Serialize(c.Input);
            yield return new ToolCallStart(id, c.Tool);
            yield return new ToolCallInputDelta(id, json[..(json.Length / 2)]);
            yield return new ToolCallInputDelta(id, json[(json.Length / 2)..]);
            yield return new ToolCallComplete(id, c.Tool, json);
            yield return new UsageReport(InputTokens: 12, OutputTokens: 8);
            yield return new StepFinished("tool_calls");
            yield break;
        }

        await foreach (var word in Words($"Echo: {lastUser}".Trim(), cancellationToken)) yield return new TextDelta(word);
        yield return new UsageReport(InputTokens: 5, OutputTokens: 5);
        yield return new StepFinished("stop");
    }

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
