using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerberus.Core.Models;
using Cerberus.Triage;

namespace Cerberus.Api.Agents;

/// <summary>
/// The code under review, as an agent may see it: one directory, read-only.
/// Every path is resolved inside that directory; links are never followed, so
/// nothing outside it can be read whatever the model — or the code it is
/// reading — asks for.
/// </summary>
public sealed class CodeWorkspace
{
    public const int MaxFileBytes = 1_000_000;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", ".venv", "venv", "__pycache__", "target", "vendor",
    };

    private readonly string _root;

    /// <param name="redact">Mask credential-shaped values in everything read (code going to a cloud model).</param>
    public CodeWorkspace(string scanRoot, string targetPath, bool redact)
    {
        var scan = Path.GetFullPath(scanRoot);
        _root = Path.GetFullPath(Path.Combine(scan, targetPath));
        if (_root != scan && !_root.StartsWith(scan + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The target is outside the scan root.");
        }
        if (!Directory.Exists(_root)) throw new InvalidOperationException($"The target \"{targetPath}\" is not a directory.");
        Redact = redact;
    }

    public bool Redact { get; }

    /// <summary>Full path of a path given relative to the target, or a refusal the model can read.</summary>
    public string Resolve(string? relative)
    {
        var path = string.IsNullOrWhiteSpace(relative) ? "." : relative.Trim();
        if (Path.IsPathRooted(path)) throw new AgentToolException("Paths are relative to the code under review; absolute paths are not allowed.");

        var full = Path.GetFullPath(Path.Combine(_root, path));
        if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new AgentToolException("That path is outside the code under review.");
        }

        // No link anywhere on the way: a link inside the target could point anywhere on this machine.
        var current = _root;
        foreach (var segment in Path.GetRelativePath(_root, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null) throw new AgentToolException("That path goes through a link, which is not followed.");
        }
        return full;
    }

    public string Relative(string full) => Path.GetRelativePath(_root, full).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>The lines of a text file, or a refusal the model can read.</summary>
    public string[] ReadLines(string? relative)
    {
        var full = Resolve(relative);
        var info = new FileInfo(full);
        if (!info.Exists) throw new AgentToolException(Directory.Exists(full) ? "That is a directory; use listDirectory." : "No such file.");
        if (info.Length > MaxFileBytes) throw new AgentToolException($"That file is too large to read ({info.Length} bytes).");
        try
        {
            var content = File.ReadAllText(full);
            if (content.Contains('\0')) throw new AgentToolException("That is a binary file.");
            var lines = content.Split('\n');
            if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1];   // the file's final newline is not a line
            return Redact ? lines.Select(TriagePrompt.Redact).ToArray() : lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AgentToolException("That file cannot be read.");
        }
    }

    /// <summary>Text files under a directory, depth first, skipping dependency and build directories and links.</summary>
    public IEnumerable<string> Files(string? relative)
    {
        var pending = new Stack<string>();
        pending.Push(Resolve(relative));
        while (pending.Count > 0)
        {
            var directory = new DirectoryInfo(pending.Pop());
            if (!directory.Exists) continue;
            FileSystemInfo[] entries;
            try
            {
                entries = directory.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (entry.LinkTarget is not null) continue;
                if (entry is DirectoryInfo sub)
                {
                    if (!SkippedDirectories.Contains(sub.Name)) pending.Push(sub.FullName);
                }
                else if (entry is FileInfo { Length: <= MaxFileBytes })
                {
                    yield return entry.FullName;
                }
            }
        }
    }

    public IEnumerable<(string Name, bool IsDirectory, long Size)> List(string? relative)
    {
        var full = Resolve(relative);
        if (!Directory.Exists(full)) throw new AgentToolException(File.Exists(full) ? "That is a file; use readFile." : "No such directory.");
        return new DirectoryInfo(full).GetFileSystemInfos()
            .Where(e => e.LinkTarget is null)
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .Select(e => (e.Name, e is DirectoryInfo, e is FileInfo f ? f.Length : 0L));
    }
}

/// <summary>Read-only tools over a <see cref="CodeWorkspace"/>: list, read, search.</summary>
public static class CodeTools
{
    public const string ListDirectory = "listDirectory";
    public const string ReadFile = "readFile";
    public const string Grep = "grep";

    public const int MaxLinesPerRead = 400;
    public const int MaxMatches = 100;
    public const int MaxEntries = 500;

    public static IReadOnlyList<IAgentTool> For(CodeWorkspace workspace) =>
        [new ListTool(workspace), new ReadTool(workspace), new GrepTool(workspace), .. CodeMapTools.For(workspace)];

    internal static ToolDefinition Define(string name, string description, string schema) =>
        new(name, description, JsonDocument.Parse(schema).RootElement.Clone());

    internal static string? Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static int? Number(JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private sealed class ListTool(CodeWorkspace workspace) : IAgentTool
    {
        public ToolDefinition Definition { get; } = Define(ListDirectory,
            "List the entries of a directory of the code under review.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Directory, relative to the code under review (default: its top)"}},"additionalProperties":false}""");

        public Task<string> RunAsync(JsonElement input, CancellationToken ct)
        {
            var entries = workspace.List(Text(input, "path")).ToList();
            return Task.FromResult(JsonSerializer.Serialize(new
            {
                entries = entries.Take(MaxEntries).Select(e => new { name = e.Name, type = e.IsDirectory ? "directory" : "file", size = e.IsDirectory ? (long?)null : e.Size }),
                more = entries.Count > MaxEntries ? entries.Count - MaxEntries : 0,
            }));
        }
    }

    private sealed class ReadTool(CodeWorkspace workspace) : IAgentTool
    {
        public ToolDefinition Definition { get; } = Define(ReadFile,
            $"Read a file of the code under review. Lines come back numbered. At most {MaxLinesPerRead} lines per call; use startLine to read further.",
            """{"type":"object","properties":{"path":{"type":"string","description":"File, relative to the code under review"},"startLine":{"type":"number","description":"First line to return (default 1)"}},"required":["path"],"additionalProperties":false}""");

        public Task<string> RunAsync(JsonElement input, CancellationToken ct)
        {
            var path = Text(input, "path") ?? throw new AgentToolException("path is required.");
            var lines = workspace.ReadLines(path);
            var start = Math.Max(1, Number(input, "startLine") ?? 1);
            if (start > lines.Length) throw new AgentToolException($"The file has only {lines.Length} line(s).");

            var text = new StringBuilder();
            var end = Math.Min(lines.Length, start + MaxLinesPerRead - 1);
            for (var i = start; i <= end; i++) text.Append(i).Append('\t').Append(lines[i - 1].TrimEnd('\r')).Append('\n');
            if (end < lines.Length) text.Append($"[{lines.Length - end} more line(s); continue with startLine {end + 1}]\n");
            return Task.FromResult(text.ToString());
        }
    }

    private sealed class GrepTool(CodeWorkspace workspace) : IAgentTool
    {
        public ToolDefinition Definition { get; } = Define(Grep,
            $"Search the code under review with a regular expression. Returns up to {MaxMatches} matching lines as path:line: text.",
            """{"type":"object","properties":{"pattern":{"type":"string","description":"Regular expression"},"path":{"type":"string","description":"Directory to search, relative to the code under review (default: all of it)"}},"required":["pattern"],"additionalProperties":false}""");

        public Task<string> RunAsync(JsonElement input, CancellationToken ct)
        {
            var pattern = Text(input, "pattern") ?? throw new AgentToolException("pattern is required.");
            Regex regex;
            try
            {
                // The pattern comes from the model: bounded, so a bad one cannot stall the run.
                regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex)
            {
                throw new AgentToolException($"Not a valid regular expression: {ex.Message}");
            }

            var text = new StringBuilder();
            var matches = 0;
            foreach (var file in workspace.Files(Text(input, "path")))
            {
                ct.ThrowIfCancellationRequested();
                string[] lines;
                try
                {
                    lines = workspace.ReadLines(workspace.Relative(file));
                }
                catch (AgentToolException)
                {
                    continue;   // binary or unreadable: nothing to search
                }
                for (var i = 0; i < lines.Length; i++)
                {
                    bool hit;
                    try
                    {
                        hit = regex.IsMatch(lines[i]);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        throw new AgentToolException("That pattern takes too long to match; use a simpler one.");
                    }
                    if (!hit) continue;
                    var line = lines[i].Trim();
                    text.Append(workspace.Relative(file)).Append(':').Append(i + 1).Append(": ")
                        .Append(line.Length <= 200 ? line : line[..200]).Append('\n');
                    if (++matches >= MaxMatches)
                    {
                        text.Append($"[stopped at {MaxMatches} matches; narrow the pattern or the path]\n");
                        return Task.FromResult(text.ToString());
                    }
                }
            }
            return Task.FromResult(matches == 0 ? "No matches." : text.ToString());
        }
    }
}
