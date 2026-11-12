using System.Security.Cryptography;
using System.Text;
using Cerberus.Api.Agents;
using Cerberus.Scanning;

namespace Cerberus.Api.Search;

/// <param name="Name">What the chunk defines ("function login"), or empty for a stretch of a file without a definition.</param>
public sealed record Chunk(string File, string FileHash, int StartLine, int EndLine, string Name, string Text);

/// <summary>
/// Cuts code into pieces worth finding on their own. A piece starts where a
/// function, class or type starts and runs to the next one — the boundaries
/// the code map already knows — so a hit is a whole definition, not an
/// arbitrary window. Files without definitions are cut into plain windows.
/// </summary>
public static class CodeChunker
{
    public const int MaxFiles = 3000;
    public const int MaxChunks = 20_000;
    public const int MaxFileBytes = 300_000;
    public const int MaxChunkLines = 120;
    public const int MaxChunkChars = 3000;
    private const int WindowLines = 60;

    /// <summary>Text files that are not source in the code map's sense but are worth searching.</summary>
    private static readonly HashSet<string> OtherText = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sql", ".sh", ".yaml", ".yml", ".toml", ".tf", ".md", ".graphql", ".proto",
    };

    public static bool IsIndexed(string file) => CodeMap.IsSource(file) || OtherText.Contains(Path.GetExtension(file));

    public static string HashOf(string[] lines) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))), 0, 12).ToLowerInvariant();

    /// <summary>The files of the target that would be indexed, with their lines and content hash.</summary>
    public static IEnumerable<(string File, string[] Lines, string Hash)> Files(CodeWorkspace workspace)
    {
        var count = 0;
        foreach (var full in workspace.Files(null))
        {
            var file = workspace.Relative(full);
            if (!IsIndexed(file) || new FileInfo(full).Length > MaxFileBytes) continue;
            string[] lines;
            try
            {
                lines = workspace.ReadLines(file);
            }
            catch (AgentToolException)
            {
                continue;   // binary or unreadable
            }
            if (lines.All(string.IsNullOrWhiteSpace)) continue;
            yield return (file, lines, HashOf(lines));
            if (++count >= MaxFiles) yield break;
        }
    }

    /// <summary>The line a definition's chunk starts at: the first of the comment, decorator or attribute lines directly above it.</summary>
    private static int WithLeadingComment(string[] lines, int definitionLine)
    {
        var start = definitionLine;
        while (start > 1)
        {
            var above = lines[start - 2].TrimStart();
            var isLead = above.StartsWith("//", StringComparison.Ordinal) || above.StartsWith('#') || above.StartsWith('*') ||
                         above.StartsWith("/*", StringComparison.Ordinal) || above.StartsWith('@') ||
                         (above.StartsWith('[') && above.TrimEnd().EndsWith(']')) || above.StartsWith("\"\"\"", StringComparison.Ordinal);
            if (!isLead) break;
            start--;
        }
        return start;
    }

    /// <summary>The chunks of one file.</summary>
    public static IReadOnlyList<Chunk> Chunk(string file, string[] lines, string hash)
    {
        // A definition's chunk begins with the comment, decorators or attributes right above it:
        // that is where the code says in words what it is for, which is what a search by meaning goes by.
        var starts = CodeMap.Outline(file, lines).Where(d => d.Kind != "route").OrderBy(d => d.Line)
            .Select(d => d with { Line = WithLeadingComment(lines, d.Line) })
            .GroupBy(d => d.Line).Select(g => g.First())   // two definitions on one line: one chunk
            .ToList();
        var ranges = new List<(int Start, int End, string Name)>();

        if (starts.Count == 0)
        {
            for (var start = 1; start <= lines.Length; start += WindowLines) ranges.Add((start, Math.Min(lines.Length, start + WindowLines - 1), ""));
        }
        else
        {
            // What comes before the first definition: imports, constants, configuration.
            if (starts[0].Line > 1) ranges.Add((1, starts[0].Line - 1, ""));
            for (var i = 0; i < starts.Count; i++)
            {
                var end = i + 1 < starts.Count ? starts[i + 1].Line - 1 : lines.Length;
                // A class is found through its methods; its own chunk is just its head, up to the first member.
                ranges.Add((starts[i].Line, Math.Max(starts[i].Line, end), $"{starts[i].Kind} {starts[i].Name}"));
            }
        }

        var chunks = new List<Chunk>();
        foreach (var (rangeStart, rangeEnd, name) in ranges)
        {
            for (var start = rangeStart; start <= rangeEnd; start += MaxChunkLines)
            {
                var end = Math.Min(rangeEnd, start + MaxChunkLines - 1);
                var body = string.Join('\n', lines.Skip(start - 1).Take(end - start + 1).Select(l => l.TrimEnd('\r'))).Trim('\n');
                if (body.Count(c => !char.IsWhiteSpace(c)) < 20) continue;   // a blank stretch or a lone brace says nothing
                if (body.Length > MaxChunkChars) body = body[..MaxChunkChars];
                // The path and the name are part of what is embedded: "auth/login.py — function check_password" carries meaning.
                var text = $"{file}{(name.Length > 0 ? $" — {name}" : "")}\n{SecretMasker.MaskText(body)}";
                chunks.Add(new Chunk(file, hash, start, end, name, text));
            }
        }
        return chunks;
    }
}
