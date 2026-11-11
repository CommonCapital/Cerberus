using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Cerberus.Core.Models;

/// <summary>A tool the model may call: its name, what it is for, and the JSON Schema of its input.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement InputSchema);

/// <summary>One piece of a conversation turn.</summary>
public abstract record ChatPart;

public sealed record TextPart(string Text) : ChatPart;

/// <summary>
/// The model's reasoning. <paramref name="Signature"/> (or <paramref name="RedactedData"/>)
/// is the provider's proof that the block is unmodified; a provider that
/// issued one needs it back, unchanged, when the conversation continues.
/// </summary>
public sealed record ReasoningPart(string Text, string? Signature = null, string? RedactedData = null) : ChatPart;

public sealed record ToolCallPart(string Id, string Name, JsonElement Input) : ChatPart;

/// <summary>The outcome of a tool call, as text (JSON for structured results).</summary>
public sealed record ToolResultPart(string ToolCallId, string Name, string Content, bool IsError = false) : ChatPart;

/// <summary>A turn of the conversation. Role is "user", "assistant" or "tool" (tool results).</summary>
public sealed record ChatTurn(string Role, IReadOnlyList<ChatPart> Parts)
{
    public static ChatTurn User(string text) => new("user", [new TextPart(text)]);
}

public sealed record ChatRequest(
    string ModelId,
    string? System,
    IReadOnlyList<ChatTurn> Turns,
    IReadOnlyList<ToolDefinition> Tools,
    ModelCredentials Credentials);

/// <summary>Something a model produced while streaming one step of a reply.</summary>
public abstract record ChatEvent;

public sealed record TextDelta(string Text) : ChatEvent;

public sealed record ReasoningDelta(string Text) : ChatEvent;

/// <summary>A reasoning block ended. Carries what must be sent back with it later.</summary>
public sealed record ReasoningEnd(string? Signature = null, string? RedactedData = null) : ChatEvent;

public sealed record ToolCallStart(string Id, string Name) : ChatEvent;

public sealed record ToolCallInputDelta(string Id, string JsonFragment) : ChatEvent;

/// <summary>A tool call with its complete input. <paramref name="InputJson"/> is the raw text the model wrote.</summary>
public sealed record ToolCallComplete(string Id, string Name, string InputJson) : ChatEvent;

public sealed record UsageReport(int? InputTokens, int? OutputTokens) : ChatEvent;

/// <summary>The step ended. Reason is the provider's own word for why ("stop", "tool_use", "length", …).</summary>
public sealed record StepFinished(string? Reason) : ChatEvent;

public static class ChatEvents
{
    /// <summary>A text-only provider seen through the full chat interface.</summary>
    public static async IAsyncEnumerable<ChatEvent> FromText(
        IAsyncEnumerable<string> deltas, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var delta in deltas.WithCancellation(ct))
        {
            yield return new TextDelta(delta);
        }
        yield return new StepFinished("stop");
    }

    /// <summary>Only the text of a full chat stream, for callers that want nothing else.</summary>
    public static async IAsyncEnumerable<string> TextOnly(
        IAsyncEnumerable<ChatEvent> events, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var e in events.WithCancellation(ct))
        {
            if (e is TextDelta text) yield return text.Text;
        }
    }

    /// <summary>The plain text of a turn (its text parts joined).</summary>
    public static string TextOf(ChatTurn turn) =>
        string.Concat(turn.Parts.OfType<TextPart>().Select(p => p.Text));
}
