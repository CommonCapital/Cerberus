using System.Text.Json;
using System.Text.Json.Nodes;
using Cerberus.Core.Models;

namespace Cerberus.Api.Chat;

/// <summary>
/// The AI-SDK "UI message" shape the CLI stores and sends: a message has an id,
/// a role and <c>parts</c> (text, reasoning, step-start, and one
/// <c>tool-&lt;name&gt;</c> part per tool call, carrying its input, state and
/// output). This converts between that shape and the provider-neutral
/// conversation the models are given — the counterpart of the TypeScript
/// server's validateUIMessages + convertToModelMessages.
/// </summary>
public static class UiMessages
{
    private const string ToolPrefix = "tool-";

    /// <summary>
    /// Stored history with the incoming messages applied: a message whose id is
    /// already known replaces the stored one (the CLI resends the assistant
    /// message once its tool calls have results), any other is appended.
    /// </summary>
    public static List<JsonObject> Merge(IEnumerable<JsonElement> stored, IEnumerable<JsonElement> incoming)
    {
        var merged = stored.Where(m => m.ValueKind == JsonValueKind.Object)
            .Select(m => JsonNode.Parse(m.GetRawText())!.AsObject()).ToList();

        foreach (var message in incoming)
        {
            if (message.ValueKind != JsonValueKind.Object) continue;
            var node = JsonNode.Parse(message.GetRawText())!.AsObject();
            var id = IdOf(node);
            var index = id is null ? -1 : merged.FindIndex(m => IdOf(m) == id);
            if (index == -1) merged.Add(node);
            else merged[index] = node;
        }
        return merged;
    }

    public static string? IdOf(JsonObject message) => message["id"] is JsonValue v && v.TryGetValue<string>(out var id) ? id : null;

    public static string RoleOf(JsonObject message) =>
        message["role"] is JsonValue v && v.TryGetValue<string>(out var role) ? role : "user";

    public static JsonArray PartsOf(JsonObject message) => message["parts"] as JsonArray ?? [];

    /// <summary>A message is valid if it has an id, a known role and a parts array.</summary>
    public static bool IsValid(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object &&
        message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
        message.TryGetProperty("role", out var role) && role.GetString() is "user" or "assistant" or "system" &&
        message.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array;

    /// <summary>True when an assistant message still has a tool call without a result.</summary>
    public static bool HasPendingToolCalls(JsonObject message) =>
        PartsOf(message).OfType<JsonObject>().Any(part =>
            IsToolPart(part) && StateOf(part) is not ("output-available" or "output-error"));

    /// <summary>
    /// The conversation as the model sees it. An assistant message becomes one
    /// assistant turn per step (split at <c>step-start</c>), each followed by a
    /// tool turn with that step's results. A tool call that never got a result
    /// is answered with an error, so the provider is never sent a dangling call.
    /// </summary>
    public static List<ChatTurn> ToTurns(IEnumerable<JsonObject> messages)
    {
        var turns = new List<ChatTurn>();
        foreach (var message in messages)
        {
            var role = RoleOf(message);
            if (role == "user")
            {
                var text = string.Concat(PartsOf(message).OfType<JsonObject>()
                    .Where(p => TypeOf(p) == "text").Select(p => StringOf(p, "text")));
                if (text.Length > 0) turns.Add(ChatTurn.User(text));
            }
            else if (role == "assistant")
            {
                AddAssistantTurns(turns, PartsOf(message).OfType<JsonObject>());
            }
            // "system" UI messages are not used by the CLI; the system prompt is built server-side.
        }
        return turns;
    }

    private static void AddAssistantTurns(List<ChatTurn> turns, IEnumerable<JsonObject> parts)
    {
        var step = new List<ChatPart>();
        var results = new List<ChatPart>();

        void Flush()
        {
            if (step.Count > 0) turns.Add(new ChatTurn("assistant", [.. step]));
            if (results.Count > 0) turns.Add(new ChatTurn("tool", [.. results]));
            step.Clear();
            results.Clear();
        }

        foreach (var part in parts)
        {
            var type = TypeOf(part);
            if (type == "step-start")
            {
                Flush();
            }
            else if (type == "text")
            {
                var text = StringOf(part, "text");
                if (text.Length > 0) step.Add(new TextPart(text));
            }
            else if (type == "reasoning")
            {
                // Only reasoning the provider signed can be sent back; anything else is display-only.
                var meta = part["providerMetadata"]?["anthropic"] as JsonObject;
                var signature = meta is null ? null : NullableString(meta, "signature");
                var redacted = meta is null ? null : NullableString(meta, "redactedData");
                if (signature is not null || redacted is not null)
                {
                    step.Add(new ReasoningPart(StringOf(part, "text"), signature, redacted));
                }
            }
            else if (IsToolPart(part))
            {
                var id = StringOf(part, "toolCallId");
                var name = type[ToolPrefix.Length..];
                if (id.Length == 0 || name.Length == 0) continue;

                var input = part["input"] is { } inputNode
                    ? JsonDocument.Parse(inputNode.ToJsonString()).RootElement.Clone()
                    : JsonDocument.Parse("{}").RootElement.Clone();
                step.Add(new ToolCallPart(id, name, input));

                results.Add(StateOf(part) switch
                {
                    "output-available" => new ToolResultPart(id, name, OutputText(part["output"])),
                    "output-error" => new ToolResultPart(id, name, StringOf(part, "errorText") is { Length: > 0 } e ? e : "The tool failed.", IsError: true),
                    _ => new ToolResultPart(id, name, "The tool call was not completed.", IsError: true),
                });
            }
        }
        Flush();
    }

    private static string OutputText(JsonNode? output) => output switch
    {
        null => "null",
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        _ => output.ToJsonString(),
    };

    public static bool IsToolPart(JsonObject part) => TypeOf(part).StartsWith(ToolPrefix, StringComparison.Ordinal);

    public static string TypeOf(JsonObject part) => StringOf(part, "type");

    private static string StateOf(JsonObject part) => StringOf(part, "state");

    private static string StringOf(JsonObject obj, string name) => NullableString(obj, name) ?? string.Empty;

    private static string? NullableString(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
}
