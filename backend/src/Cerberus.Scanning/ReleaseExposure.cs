using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cerberus.Scanning;

/// <summary>Something a build output gives away that the build did not need to ship.</summary>
public sealed record ReleaseIssue(
    string RuleId,
    string Title,
    Severity Severity,
    string Cwe,
    string Confidence,
    string Evidence,
    string Remediation,
    int Line = 1);

/// <summary>
/// What a release exposes (PLAN.md §23, B3): checks on built output rather than
/// source — the files a line-based source scan skips. Static and in-process;
/// nothing is executed.
///
/// - Source maps, as a <c>.map</c> file or embedded in a script or stylesheet:
///   with the original sources inside, or naming them only.
/// - Debug companions: a <c>.pdb</c> next to the build, a <c>.dSYM</c> bundle.
/// - Build-machine paths (a home directory) inside binaries and source maps.
/// - Token-shaped credentials in minified bundles and source maps, where the
///   line rules do not look because the lines are too long.
///
/// What it cannot say: whether the file is actually published — it reports
/// what is in the directory it was pointed at. Paths stored as UTF-16 in a
/// binary are not found.
/// </summary>
public static class ReleaseExposure
{
    public const string Category = "release";
    public const string Owasp = "A05";   // Security Misconfiguration

    /// <summary>Larger bundles and maps are not read; they are counted as skipped.</summary>
    public const int MaxTextBytes = 16 * 1024 * 1024;

    private const int MaxCredentialIssues = 50;

    private const string StripMaps = "Do not publish source maps with the build, or publish them without sourcesContent to an access-controlled location (for example an error-tracking service).";

    /// <summary>A minified bundle or a source map: built text the line rules skip.</summary>
    public static bool IsBuiltText(string fileName) =>
        fileName.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".min.mjs", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase);

    /// <summary>File types that may end in a <c>sourceMappingURL=data:</c> comment.</summary>
    public static bool MayCarryInlineMap(string extension) =>
        extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".css", StringComparison.OrdinalIgnoreCase);

    /// <summary>Everything this class checks in a minified bundle or a source map.</summary>
    public static IReadOnlyList<ReleaseIssue> InspectBuiltText(string fileName, string text)
    {
        var issues = new List<ReleaseIssue>();
        if (fileName.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
        {
            // A source map's own findings include the credentials in its sources, read decoded;
            // something else called .map is searched as it is.
            var map = SourceMap(text, inline: false);
            issues.AddRange(map.Count > 0 ? map : Credentials(text));
            return issues;
        }
        if (InlineSourceMap(text) is { } inline) issues.AddRange(inline);
        issues.AddRange(Credentials(text));
        return issues;
    }

    // --- source maps -------------------------------------------------------------

    /// <summary>
    /// Findings for a source map's JSON, including credentials in the sources it
    /// carries; empty when the text is not a source map.
    /// </summary>
    public static IReadOnlyList<ReleaseIssue> SourceMap(string json, bool inline)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return []; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out _) ||
                !(root.TryGetProperty("mappings", out _) || root.TryGetProperty("sections", out _)))
            {
                return [];
            }

            var names = new List<string>();
            var contents = new StringBuilder();
            var embedded = 0;
            string? example = null;
            Collect(root, names, contents, ref embedded, ref example);
            // An index map keeps its maps one level down.
            if (root.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
            {
                foreach (var section in sections.EnumerateArray())
                {
                    if (section.ValueKind == JsonValueKind.Object && section.TryGetProperty("map", out var map) && map.ValueKind == JsonValueKind.Object)
                        Collect(map, names, contents, ref embedded, ref example);
                }
            }

            var where = inline ? "An embedded source map" : "The source map";
            var issues = new List<ReleaseIssue>();
            if (embedded > 0)
            {
                issues.Add(new("release.source-map-sources", "Source map ships the original source code", Severity.Medium, "CWE-540", "high",
                    $"{where} carries the full text of {embedded} of {names.Count} source file(s) in sourcesContent{(example is null ? "" : $", for example {Show(example)}")}.",
                    StripMaps));
            }
            else
            {
                issues.Add(new("release.source-map", "Source map is part of the build output", Severity.Low, "CWE-540", "high",
                    $"{where} names {names.Count} source file(s){(names.Count > 0 ? $", for example {Show(names[0])}" : "")}; it reveals the project's structure and makes the minified code readable.",
                    StripMaps));
            }

            // Where the map was built: absolute paths among its sources.
            var paths = new StringBuilder();
            foreach (var name in names) paths.Append(name).Append('\n');
            if (root.TryGetProperty("sourceRoot", out var sourceRoot) && sourceRoot.ValueKind == JsonValueKind.String)
                paths.Append(sourceRoot.GetString()).Append('\n');
            if (BuildPaths(Encoding.UTF8.GetBytes(paths.ToString()), "high") is { } buildPath) issues.Add(buildPath);

            // The sources are searched decoded: in the JSON a token may follow an escape (\n, \u0027) and be missed.
            issues.AddRange(Credentials(contents.ToString(), within: "in the embedded sources"));
            return issues;
        }
    }

    private static void Collect(JsonElement map, List<string> names, StringBuilder contents, ref int embedded, ref string? example)
    {
        var first = names.Count;
        if (map.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sources.EnumerateArray())
                names.Add(s.ValueKind == JsonValueKind.String ? s.GetString()! : "");
        }
        if (map.TryGetProperty("sourcesContent", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var c in content.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.String && c.GetString()!.Length > 0)
                {
                    embedded++;
                    contents.Append(c.GetString()).Append('\n');
                    if (example is null && first + i < names.Count && names[first + i].Length > 0) example = names[first + i];
                }
                i++;
            }
        }
    }

    private const string InlineMarker = "sourceMappingURL=data:application/json";

    /// <summary>
    /// Findings for a source map embedded as a <c>data:</c> URL in a script or
    /// stylesheet; null when there is none (or it is not base64 JSON).
    /// </summary>
    public static IReadOnlyList<ReleaseIssue>? InlineSourceMap(string text)
    {
        var at = text.LastIndexOf(InlineMarker, StringComparison.Ordinal);
        if (at < 0) return null;
        var start = at + InlineMarker.Length;
        var comma = text.IndexOf(',', start, Math.Min(64, text.Length - start));
        if (comma < 0 || !text.AsSpan(start, comma - start).Contains("base64", StringComparison.Ordinal)) return null;

        var end = comma + 1;
        while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '+' or '/' or '=')) end++;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(text.Substring(comma + 1, end - comma - 1)));
            var issues = SourceMap(json, inline: true);
            return issues.Count > 0 ? issues : null;
        }
        catch (FormatException) { return null; }
    }

    // --- debug companions ----------------------------------------------------------

    /// <summary>A program database: the classic format's signature, or a portable PDB's metadata root.</summary>
    public static bool IsPdb(string fileName, ReadOnlySpan<byte> head) =>
        fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) &&
        (head.StartsWith("Microsoft C/C++ MSF 7.00"u8) || head.StartsWith("BSJB"u8));

    /// <summary>The <c>.dSYM</c> bundle a path lies in (its path, without a trailing slash), or null.</summary>
    public static string? DsymBundleOf(string relativePath)
    {
        var at = relativePath.IndexOf(".dSYM/", StringComparison.Ordinal);
        return at < 0 ? null : relativePath[..(at + ".dSYM".Length)];
    }

    public static ReleaseIssue DebugSymbols(string what) =>
        new("release.debug-symbols", "Debug symbols are part of the build output", Severity.Low, "CWE-215", "high",
            $"{what}: it maps the shipped code back to function names, source files and line numbers.",
            "Keep debug symbols out of what is published; store them where crash reports are symbolised.");

    // --- build-machine paths ---------------------------------------------------------

    private static readonly (byte[] Anchor, bool Windows)[] HomeAnchors =
    [
        ("/home/"u8.ToArray(), false),
        ("/Users/"u8.ToArray(), false),
        (":\\Users\\"u8.ToArray(), true),
        (":\\\\Users\\\\"u8.ToArray(), true),   // as written inside JSON
    ];

    // Names that are examples or shared locations, not someone's build machine.
    private static readonly HashSet<string> NotAPerson = new(StringComparer.OrdinalIgnoreCase)
    {
        "user", "username", "you", "me", "name", "foo", "bar", "example", "shared", "public", "default", "all users",
    };

    private const int MaxAnchorHits = 10_000;

    /// <summary>
    /// A home-directory path left in an artefact by the machine that built it
    /// (<c>/home/alice/…</c>, <c>/Users/alice/…</c>, <c>C:\Users\alice\…</c>);
    /// null when there is none.
    /// </summary>
    public static ReleaseIssue? BuildPaths(ReadOnlySpan<byte> data, string confidence = "medium")
    {
        var owners = new HashSet<string>(StringComparer.Ordinal);
        string? example = null;

        foreach (var (anchor, windows) in HomeAnchors)
        {
            var from = 0;
            for (var hits = 0; hits < MaxAnchorHits; hits++)
            {
                var found = data[from..].IndexOf(anchor);
                if (found < 0) break;
                var at = from + found;
                from = at + anchor.Length;

                // The path has to start here, not be the tail of a longer one (a URL route such as /app/home/…).
                var start = at;
                if (windows)
                {
                    if (at == 0 || !char.IsAsciiLetter((char)data[at - 1])) continue;
                    start = at - 1;
                }
                if (start > 0 && IsPathByte(data[start - 1]) && !EndsWithFileScheme(data[..start])) continue;

                var nameStart = at + anchor.Length;
                var nameEnd = nameStart;
                while (nameEnd < data.Length && nameEnd - nameStart <= 32 && IsNameByte(data[nameEnd])) nameEnd++;
                if (nameEnd == nameStart || nameEnd - nameStart > 32 || nameEnd >= data.Length) continue;
                if (data[nameEnd] is not ((byte)'/' or (byte)'\\')) continue;

                var owner = Encoding.ASCII.GetString(data[nameStart..nameEnd]);
                if (NotAPerson.Contains(owner)) continue;
                owners.Add(owner);

                if (example is null)
                {
                    var end = nameEnd;
                    while (end < data.Length && end - start < 120 && (IsPathByte(data[end]) || data[end] == (byte)' ')) end++;
                    example = Encoding.ASCII.GetString(data[start..end]).TrimEnd();
                }
            }
        }

        return example is null ? null : new ReleaseIssue(
            "release.build-path", "Build-machine path left in the artefact", Severity.Low, "CWE-200", confidence,
            $"Paths under {owners.Count} home director{(owners.Count == 1 ? "y" : "ies")} are embedded, for example {Show(example)}. They name an account on the build machine and its directory layout.",
            "Build with path remapping (for example -ffile-prefix-map, -trimpath, PathMap / deterministic builds) or in a neutral build directory.");
    }

    private static bool IsNameByte(byte b) =>
        char.IsAsciiLetterOrDigit((char)b) || b is (byte)'.' or (byte)'_' or (byte)'-';

    private static bool IsPathByte(byte b) =>
        IsNameByte(b) || b is (byte)'/' or (byte)'\\' or (byte)':' or (byte)'+' or (byte)'@' or (byte)'~';

    private static bool EndsWithFileScheme(ReadOnlySpan<byte> before) =>
        before.EndsWith("file://"u8) || before.EndsWith("file:"u8);

    // --- credentials in built text -----------------------------------------------------

    // Only the rules that match a token's own shape: in a megabyte of minified code the
    // looser ones (an assignment to something called "token") are noise.
    private static readonly IReadOnlyList<Rule> TokenRules =
        Rules.All.Where(r => r.Category == "secret" && r.Confidence == "high").ToList();

    private const int Window = 4096, Overlap = 256;

    /// <summary>
    /// Token-shaped credentials anywhere in built text, however long its lines:
    /// one issue per kind of credential and line, with the number of occurrences.
    /// The evidence is masked; the credential stays in the file.
    /// </summary>
    /// <param name="within">
    /// When set, the text is not the file itself (a source map's decoded sources):
    /// its line numbers mean nothing, so there is one issue per kind of credential,
    /// on line 1, described as being <paramref name="within"/> that place.
    /// </param>
    public static IReadOnlyList<ReleaseIssue> Credentials(string text, string? within = null)
    {
        var seen = new HashSet<(string Rule, int At)>();
        var found = new Dictionary<(string Rule, int Line), (Rule Rule, int Count, string Evidence)>();
        var order = new List<(string Rule, int Line)>();

        var lineStart = 0;
        for (var line = 1; lineStart <= text.Length; line++)
        {
            var newline = text.IndexOf('\n', lineStart);
            var lineEnd = newline < 0 ? text.Length : newline;

            for (var w = lineStart; w < lineEnd; w += Window - Overlap)
            {
                var length = Math.Min(Window, lineEnd - w);
                var window = text.Substring(w, length);
                foreach (var rule in TokenRules)
                {
                    MatchCollection matches;
                    try
                    {
                        matches = rule.Pattern.Matches(window);
                        _ = matches.Count;   // the search runs here, inside the try
                    }
                    catch (RegexMatchTimeoutException) { continue; }

                    foreach (Match m in matches)
                    {
                        if (!seen.Add((rule.Id, w + m.Index))) continue;   // the same token, seen in the overlap
                        var key = (rule.Id, within is null ? line : 1);
                        if (found.TryGetValue(key, out var entry))
                        {
                            found[key] = entry with { Count = entry.Count + 1 };
                            continue;
                        }
                        if (found.Count >= MaxCredentialIssues) continue;
                        var from = Math.Max(lineStart, w + m.Index - 30);
                        var to = Math.Min(lineEnd, w + m.Index + m.Length + 30);
                        found[key] = (rule, 1, SecretMasker.Mask(text[from..to]));
                        order.Add(key);
                    }
                }
                if (length < Window) break;
            }

            if (newline < 0) break;
            lineStart = newline + 1;
        }

        return order.Select(key =>
        {
            var (rule, count, evidence) = found[key];
            return new ReleaseIssue(
                "release.credential." + rule.Id["secret.".Length..],
                $"Credential in built output: {rule.Title}", rule.Severity, rule.Cwe ?? "CWE-798", "high",
                $"{(count == 1 ? "1 occurrence" : $"{count} occurrences")} {within ?? "on this line"}; first: {evidence}",
                "Treat the credential as disclosed: revoke and replace it, then keep it out of the build — client-side code and its maps are readable by everyone who receives them.",
                key.Line);
        }).ToList();
    }

    /// <summary>A name from the artefact, shown without control characters and cut to a sensible length.</summary>
    private static string Show(string value)
    {
        var clean = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return clean.Length <= 100 ? clean : clean[..100] + "…";
    }
}
