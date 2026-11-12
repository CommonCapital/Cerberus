using System.Text;
using System.Text.Json.Nodes;
using Cerberus.Data;
using Cerberus.Scanning;

namespace Cerberus.Api.Chat;

/// <summary>
/// A conversation as a Markdown document: what was asked, what the agent
/// said, and what it did — each tool call with what it was about and how it
/// went. Written to be read by a person and to travel (a ticket, a review),
/// so credential-shaped values are masked, long tool output is shortened and
/// the model's private reasoning is left out.
/// </summary>
public static class SessionExport
{
    public const string ContentType = "text/markdown; charset=utf-8";

    private const int MaxDiffLines = 60;
    private const int MaxOutputLines = 20;
    private const int MaxInline = 200;

    public static string FileName(Session session) => $"cerberus-chat-{session.Id[..Math.Min(8, session.Id.Length)]}.md";

    public static string Markdown(Session session, IReadOnlyList<JsonObject> messages, DateTime exportedAt)
    {
        var text = new StringBuilder();
        text.Append("# ").AppendLine(OneLine(Mask(session.Title.Length > 0 ? session.Title : "Conversation")));
        text.AppendLine();
        text.AppendLine($"Exported {StoredTime.Utc(exportedAt):yyyy-MM-dd HH:mm} UTC from Cerberus. Started {StoredTime.Utc(session.CreatedAt):yyyy-MM-dd HH:mm} UTC; {messages.Count} message{(messages.Count == 1 ? "" : "s")}.");
        text.AppendLine();
        text.AppendLine("> Values that look like credentials are masked. Long tool output is shortened, and the model's reasoning is not included.");

        foreach (var message in messages)
        {
            text.AppendLine();
            var metadata = message["metadata"] as JsonObject;
            if (UiMessages.RoleOf(message) == "user")
            {
                if (metadata?["shell"] is JsonObject shell) AppendShell(text, shell);
                else
                {
                    text.AppendLine(metadata?["note"] is not null ? "## Note to the agent" : "## You");
                    text.AppendLine();
                    text.AppendLine(Mask(TextOf(message)));
                }
                continue;
            }

            var who = new[] { String(metadata, "model"), String(metadata, "mode")?.ToLowerInvariant() }.Where(v => !string.IsNullOrEmpty(v)).ToList();
            text.AppendLine(who.Count > 0 ? $"## Assistant ({string.Join(", ", who)})" : "## Assistant");
            AppendAssistant(text, message);
        }
        return text.ToString();
    }

    private static void AppendShell(StringBuilder text, JsonObject shell)
    {
        text.AppendLine("## You ran a command");
        text.AppendLine();
        var output = Mask(String(shell, "output") ?? "");
        AppendFence(text, "", $"$ {Mask(String(shell, "command") ?? "")}" + (output.Length > 0 ? "\n" + Tail(output, MaxOutputLines) : ""));
        text.AppendLine(shell["timedOut"] is not null ? "It was stopped: it ran too long." : $"Exit code {shell["exitCode"]?.ToString() ?? "?"}.");
    }

    private static void AppendAssistant(StringBuilder text, JsonObject message)
    {
        // Tool calls that follow one another form one list; text stands as paragraphs between them.
        var inList = false;
        foreach (var part in UiMessages.PartsOf(message).OfType<JsonObject>())
        {
            var type = UiMessages.TypeOf(part);
            if (type == "text")
            {
                var said = (part["text"]?.GetValue<string>() ?? "").Trim();
                if (said.Length == 0) continue;
                text.AppendLine();
                text.AppendLine(Mask(said));
                inList = false;
            }
            else if (UiMessages.IsToolPart(part))
            {
                if (!inList) text.AppendLine();
                inList = true;
                AppendToolCall(text, type["tool-".Length..], part);
            }
        }
    }

    private static void AppendToolCall(StringBuilder text, string tool, JsonObject part)
    {
        var input = part["input"] as JsonObject;
        var output = part["output"] as JsonObject;
        var state = String(part, "state");

        var about = tool switch
        {
            "readFile" or "writeFile" or "editFile" or "listDirectory" => String(input, "path"),
            "grep" or "glob" => String(input, "pattern"),
            "bash" => String(input, "command"),
            "searchSkills" or "searchCode" => String(input, "query"),
            "loadSkill" => String(input, "name"),
            "task" => String(input, "description"),
            "updateTasks" => null,
            _ => input?.ToJsonString(),
        };
        text.Append("- **").Append(tool).Append("**");
        if (!string.IsNullOrWhiteSpace(about)) text.Append(' ').Append(Code(Shorten(OneLine(Mask(about)), MaxInline)));

        if (state == "output-error")
        {
            text.Append(" — failed: ").AppendLine(Shorten(OneLine(Mask(String(part, "errorText") ?? "")), 300));
            return;
        }
        if (state != "output-available")
        {
            text.AppendLine(" — not finished");
            return;
        }

        var outcome = tool switch
        {
            "readFile" when Number(output, "startLine") is { } start && Number(output, "endLine") is { } end =>
                $"lines {start}–{end}" + (Number(output, "totalLines") is { } total ? $" of {total}" : ""),
            "grep" when output?["matches"] is JsonArray matches => $"{matches.Count} match{(matches.Count == 1 ? "" : "es")}",
            "glob" when output?["files"] is JsonArray files => $"{files.Count} file{(files.Count == 1 ? "" : "s")}",
            "listDirectory" when output?["entries"] is JsonArray entries => $"{entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}",
            "editFile" => $"{Number(output, "replacements") ?? 1} replacement(s)",
            "writeFile" => output?["created"]?.GetValue<bool>() == true ? "created" : "overwritten",
            "bash" => output?["timedOut"] is not null ? "stopped: it ran too long" : $"exit code {Number(output, "exitCode")?.ToString() ?? "?"}",
            "task" => $"helper {(output?["complete"]?.GetValue<bool>() == true ? "finished" : "stopped")}, {Number(output, "toolCalls") ?? 0} tool call(s)",
            "updateTasks" => String(output, "summary"),
            _ => null,
        };
        text.AppendLine(string.IsNullOrEmpty(outcome) ? "" : $" — {outcome}");

        // What a call changed or produced, where that is the point of it.
        if (tool is "editFile" or "writeFile" && String(output, "diff") is { Length: > 0 } diff)
        {
            AppendFence(text, "diff", Head(Mask(diff), MaxDiffLines), indent: "  ");
        }
        else if (tool == "bash")
        {
            var printed = string.Join("\n", new[] { String(output, "stdout"), String(output, "stderr") }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.TrimEnd()));
            if (printed.Length > 0) AppendFence(text, "", Tail(Mask(printed), MaxOutputLines), indent: "  ");
        }
        else if (tool == "task" && String(output, "report") is { Length: > 0 } report)
        {
            foreach (var line in Mask(report).Split('\n')) text.Append("  > ").AppendLine(line.TrimEnd());
        }
    }

    /// <summary>A fenced block whose fence is longer than any run of backticks inside it, so the content cannot close it.</summary>
    private static void AppendFence(StringBuilder text, string language, string content, string indent = "")
    {
        var longest = 0;
        var run = 0;
        foreach (var c in content)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        text.Append(indent).Append(fence).AppendLine(language);
        foreach (var line in content.Split('\n')) text.Append(indent).AppendLine(line.TrimEnd('\r'));
        text.Append(indent).AppendLine(fence);
    }

    private static string Head(string value, int lines)
    {
        var all = value.Split('\n');
        return all.Length <= lines ? value : string.Join("\n", all.Take(lines)) + $"\n… {all.Length - lines} more line(s)";
    }

    private static string Tail(string value, int lines)
    {
        var all = value.Split('\n');
        return all.Length <= lines ? value : $"… {all.Length - lines} earlier line(s)\n" + string.Join("\n", all.Skip(all.Length - lines));
    }

    private static string TextOf(JsonObject message) =>
        string.Concat(UiMessages.PartsOf(message).OfType<JsonObject>().Where(p => UiMessages.TypeOf(p) == "text").Select(p => p["text"]?.GetValue<string>() ?? "")).Trim();

    private static string Mask(string value) => SecretMasker.MaskText(value);

    private static string OneLine(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Shorten(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    /// <summary>Inline code that survives backticks in the value.</summary>
    private static string Code(string value) => value.Contains('`') ? $"`` {value} ``" : $"`{value}`";

    private static string? String(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static long? Number(JsonObject? node, string name) =>
        node?[name] is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;
}
