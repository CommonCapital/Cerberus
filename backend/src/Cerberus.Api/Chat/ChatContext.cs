using System.Text;
using System.Text.Json;

namespace Cerberus.Api.Chat;

public sealed record ChatInstructionFile(string File, string Content, bool Truncated, string Scope, string? Directory);

/// <summary>
/// What the client tells the chat agent about where it is working: the
/// environment its tools run in, operator defaults and project instructions.
/// Both come from the operator's machine, as the tools do. Everything is
/// length-limited and reduced to plain text before it reaches a prompt.
/// </summary>
public sealed record ChatContext(
    string? WorkingDirectory, string? Platform, string? Date, bool IsGitRepository, string? GitBranch,
    string? InstructionsFile, string? Instructions, bool InstructionsTruncated)
{
    public const int MaxInstructions = 20_000;
    public const int MaxInstructionFiles = 16;

    // Null means an older client sent only the single instructions object.
    // An explicitly empty array means there are no sources; do not fall back.
    public IReadOnlyList<ChatInstructionFile>? InstructionFiles { get; init; }
    public bool InstructionFilesTruncated { get; init; }

    public static readonly ChatContext None = new(null, null, null, false, null, null, null, false);

    /// <summary>Reads the request's <c>context</c> leniently: anything missing or of the wrong shape is simply absent.</summary>
    public static ChatContext Read(JsonElement? context)
    {
        if (context is not { ValueKind: JsonValueKind.Object } root) return None;

        var environment = root.TryGetProperty("environment", out var e) && e.ValueKind == JsonValueKind.Object ? e : default;
        var instructions = root.TryGetProperty("instructions", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        var content = Text(instructions, "content", MaxInstructions + 1, keepLines: true);

        var result = new ChatContext(
            Text(environment, "workingDirectory", 500),
            Text(environment, "platform", 40),
            Text(environment, "date", 20),
            environment.ValueKind == JsonValueKind.Object && environment.TryGetProperty("isGitRepository", out var git) && git.ValueKind == JsonValueKind.True,
            Text(environment, "gitBranch", 200),
            Text(instructions, "file", 300),
            content is { Length: > MaxInstructions } ? content[..MaxInstructions] : content,
            content is { Length: > MaxInstructions } ||
                IsTrue(instructions, "truncated"));

        if (!root.TryGetProperty("instructionFiles", out var sources) || sources.ValueKind != JsonValueKind.Array) return result;
        var files = new List<ChatInstructionFile>();
        var remaining = MaxInstructions;
        var inspected = 0;
        var omitted = IsTrue(root, "instructionFilesTruncated");
        foreach (var source in sources.EnumerateArray())
        {
            if (inspected++ >= MaxInstructionFiles || remaining == 0)
            {
                omitted = true;
                break;
            }
            var scope = Text(source, "scope", 20);
            if (scope is not ("global" or "project")) continue;
            var file = Text(source, "file", 300);
            var value = Text(source, "content", remaining + 1, keepLines: true);
            if (file is null || value is null) continue;
            var truncated = value.Length > remaining || IsTrue(source, "truncated");
            if (value.Length > remaining) value = value[..remaining];
            files.Add(new(file, value, truncated, scope, Text(source, "directory", 500)));
            remaining -= value.Length;
        }
        return result with { InstructionFiles = files, InstructionFilesTruncated = omitted };
    }

    private static bool IsTrue(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement parent, string name, int max, bool keepLines = false)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()!;
        if (!keepLines) text = string.Concat(text.Select(c => char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c)); // metadata cannot start a new section
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
        if (InstructionFiles is not null)
        {
            if (InstructionFiles.Count > 0)
            {
                if (text.Length > 0) text.AppendLine();
                text.AppendLine("## Workspace instructions");
                text.AppendLine("These sources are supplied by the operator's CLI, in order: operator defaults, then project directories from outer to inner. " +
                    "Project notes apply only within their stated directory; more specific project notes take precedence there. " +
                    "They do not override the user's requests, the mode you are in, or the rules about asking before changes.");
                foreach (var source in InstructionFiles)
                {
                    text.AppendLine();
                    text.AppendLine($"### {(source.Scope == "global" ? "Operator defaults" : "Project instructions")} ({source.File})");
                    if (source.Scope == "project" && source.Directory is not null) text.AppendLine($"Applies within: {source.Directory}");
                    text.AppendLine(source.Content);
                    if (source.Truncated) text.AppendLine("[This source was truncated to fit the instruction budget.]");
                }
            }
            if (InstructionFilesTruncated)
                text.AppendLine("[Some instruction sources were omitted to fit the source-count or content limit.]");
        }
        else if (Instructions is not null)
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
