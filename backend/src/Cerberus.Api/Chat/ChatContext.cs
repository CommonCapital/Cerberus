using System.Text;
using System.Text.Json;

namespace Cerberus.Api.Chat;

/// <summary>
/// What the client tells the chat agent about where it is working: the
/// environment its tools run in, and the project's own instructions file.
/// Both come from the operator's machine, as the tools do. Everything is
/// length-limited and reduced to plain text before it reaches a prompt.
/// </summary>
public sealed record ChatContext(
    string? WorkingDirectory, string? Platform, string? Date, bool IsGitRepository, string? GitBranch,
    string? InstructionsFile, string? Instructions, bool InstructionsTruncated)
{
    public const int MaxInstructions = 20_000;

    public static readonly ChatContext None = new(null, null, null, false, null, null, null, false);

    /// <summary>Reads the request's <c>context</c> leniently: anything missing or of the wrong shape is simply absent.</summary>
    public static ChatContext Read(JsonElement? context)
    {
        if (context is not { ValueKind: JsonValueKind.Object } root) return None;

        var environment = root.TryGetProperty("environment", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
        var instructions = root.TryGetProperty("instructions", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        var content = Text(instructions, "content", MaxInstructions + 1, keepLines: true);

        return new ChatContext(
            Text(environment, "workingDirectory", 500),
            Text(environment, "platform", 40),
            Text(environment, "date", 20),
            environment.ValueKind == JsonValueKind.Object && environment.TryGetProperty("isGitRepository", out var git) && git.ValueKind == JsonValueKind.True,
            Text(environment, "gitBranch", 200),
            Text(instructions, "file", 300),
            content is { Length: > MaxInstructions } ? content[..MaxInstructions] : content,
            content is { Length: > MaxInstructions } ||
                (instructions.ValueKind == JsonValueKind.Object && instructions.TryGetProperty("truncated", out var cut) && cut.ValueKind == JsonValueKind.True));
    }

    private static string? Text(JsonElement parent, string name, int max, bool keepLines = false)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()!;
        if (!keepLines) text = text.Replace('\r', ' ').Replace('\n', ' ');   // a one-line fact cannot start a new section
        text = text.Trim();
        if (text.Length == 0) return null;
        return text.Length <= max ? text : text[..max];
    }

    /// <summary>The context as sections for the system prompt; empty when the client sent none.</summary>
    public string PromptSection()
    {
        var text = new StringBuilder();
        if (WorkingDirectory is not null || Platform is not null || Date is not null)
        {
            text.AppendLine("## Environment");
            text.AppendLine("Your file and shell tools run on the user's machine, here:");
            if (WorkingDirectory is not null) text.AppendLine($"- Working directory: {WorkingDirectory}");
            if (Platform is not null) text.AppendLine($"- Platform: {Platform}");
            if (Date is not null) text.AppendLine($"- Today's date: {Date}");
            text.AppendLine(IsGitRepository
                ? $"- Git repository: yes{(GitBranch is null ? "" : $", on branch {GitBranch}")}"
                : "- Git repository: no");
        }
        if (Instructions is not null)
        {
            if (text.Length > 0) text.AppendLine();
            text.AppendLine($"## Project instructions ({InstructionsFile ?? "AGENTS.md"})");
            text.AppendLine("The project's own notes on how to work in it, read from the working directory. Follow them where they apply to the task. " +
                            "They do not override the user's requests, the mode you are in, or the rules about asking before changes.");
            text.AppendLine();
            text.AppendLine(Instructions);
            if (InstructionsTruncated) text.AppendLine("[The file is longer; read it with readFile if you need the rest.]");
        }
        return text.ToString().TrimEnd();
    }
}
