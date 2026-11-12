using System.Text;
using System.Text.RegularExpressions;

namespace Cerberus.Scanning;

/// <param name="FilesScanned">Text files the rules were applied to.</param>
/// <param name="Findings">All findings, including suppressed ones (flagged), most severe first.</param>
/// <param name="FilesSkipped">Files not scanned: binary, too large, generated, or unreadable.</param>
/// <param name="FilesIgnored">Files excluded by a <c>.cerberusignore</c> file.</param>
/// <param name="SuppressedCount">Findings the target marked with <c>cerberus:ignore</c>.</param>
/// <param name="Truncated">True when the finding cap was reached and the scan stopped early.</param>
public sealed record ScanResult(
    int FilesScanned,
    IReadOnlyList<Finding> Findings,
    int FilesSkipped = 0,
    int FilesIgnored = 0,
    int SuppressedCount = 0,
    bool Truncated = false)
{
    /// <summary>Findings that count: everything the target did not suppress.</summary>
    public int ActiveCount => Findings.Count - SuppressedCount;
}

/// <summary>
/// Phase-1 passive scanner: walks a target directory inside the configured
/// scan root, applies the regex rule set per line, and returns normalized
/// findings. Trusted-target MVP — real engagements run this inside the sandbox
/// (see PLAN.md M3/M5). Paths are confined to the scan root.
/// </summary>
public sealed partial class ScanService(ScanOptions options)
{
    public const string IgnoreFileName = ".cerberusignore";
    public const string SuppressionMarker = "cerberus:ignore";

    private static readonly HashSet<string> SkipDirs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Dependency, build-output and tool directories. Not "packages": that is where
            // monorepos keep their own source.
            "node_modules", ".git", "bin", "obj", "dist", ".vs", "vendor", ".venv", "venv",
            "__pycache__", "_build", ".erlang.mk", "target", ".idea", ".gradle",
        };

    // Not source: binaries, media, archives, and generated or lock files full of hashes.
    private static readonly HashSet<string> SkipExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".ico", ".svg", ".webp", ".bmp", ".pdf", ".zip", ".gz", ".tgz", ".tar",
            ".bz2", ".xz", ".7z", ".jar", ".war", ".class", ".dll", ".exe", ".so", ".dylib", ".o", ".a", ".beam",
            ".ez", ".woff", ".woff2", ".ttf", ".eot", ".mp3", ".mp4", ".mov", ".db", ".sqlite", ".lock", ".map",
            ".min.js", ".min.css", ".snap", ".xlsx", ".docx", ".pptx",
        };

    private static readonly HashSet<string> SkipFileNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "bun.lock", "bun.lockb", "Cargo.lock", "go.sum",
            "poetry.lock", "Pipfile.lock", "composer.lock", "Gemfile.lock", "mix.lock", "rebar.lock",
        };

    private const int MaxFileBytes = 1_000_000;
    private const int MaxLineLength = 2_000;      // longer lines are minified or data, not code
    private const int MaxFindings = 5_000;

    /// <param name="honourExclusions">
    /// When false, the target's own <c>.cerberusignore</c> files and
    /// <c>cerberus:ignore</c> markers are disregarded: every file is scanned and
    /// no finding is suppressed. Use it for code you do not trust.
    /// </param>
    public ScanResult Scan(string relativePath, CancellationToken ct = default, bool honourExclusions = true)
    {
        var root = Path.GetFullPath(options.ScanRoot);
        var target = Path.GetFullPath(Path.Combine(root, relativePath));

        // Confinement: the resolved target must stay within the scan root.
        if (!target.Equals(root, StringComparison.Ordinal) &&
            !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Path is outside the scan root.");
        }

        var ignore = honourExclusions ? IgnoreList.Load(root, target) : IgnoreList.Empty;
        var findings = new List<Finding>();
        int filesScanned = 0, filesSkipped = 0, filesIgnored = 0, suppressed = 0;
        var truncated = false;

        foreach (var file in EnumerateFiles(target))
        {
            ct.ThrowIfCancellationRequested();   // cooperative stop between files
            if (findings.Count >= MaxFindings) { truncated = true; break; }

            var relFile = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (ignore.Matches(relFile)) { filesIgnored++; continue; }
            if (!LooksScannable(file)) { filesSkipped++; continue; }

            string[] lines;
            try
            {
                var bytes = File.ReadAllBytes(file);
                if (IsBinary(bytes)) { filesSkipped++; continue; }
                lines = Encoding.UTF8.GetString(bytes).Split('\n');
            }
            catch (IOException) { filesSkipped++; continue; }
            catch (UnauthorizedAccessException) { filesSkipped++; continue; }

            filesScanned++;
            var context = ContextOf(relFile);
            var extension = Path.GetExtension(file);
            var rules = Rules.All.Where(r => r.AppliesTo(extension)).ToList();

            for (var i = 0; i < lines.Length && findings.Count < MaxFindings; i++)
            {
                var line = lines[i];
                if (line.Length > MaxLineLength) continue;

                foreach (var rule in rules)
                {
                    bool match;
                    try { match = rule.Pattern.IsMatch(line); }
                    catch (RegexMatchTimeoutException) { continue; }
                    if (!match) continue;

                    var isSuppressed = honourExclusions && (IsSuppressed(rule.Id, line) || (i > 0 && IsSuppressed(rule.Id, lines[i - 1], commentOnly: true)));
                    if (isSuppressed) suppressed++;

                    findings.Add(new Finding(
                        Id: $"{rule.Id}:{relFile}:{i + 1}",
                        Title: rule.Title,
                        Severity: rule.Severity,
                        Category: rule.Category,
                        Cwe: rule.Cwe,
                        File: relFile,
                        Line: i + 1,
                        // Masked before it is cut, so no part of a secret survives the cut; the secret stays in the file.
                        Evidence: Truncate(SecretMasker.Mask(line.Trim()), 200),
                        // A hit in test code or documentation is far more often an example than a defect.
                        Confidence: context is null ? rule.Confidence : "low",
                        Owasp: Rules.OwaspFor(rule.Category),
                        Remediation: rule.Remediation,
                        Context: context,
                        Suppressed: isSuppressed));

                    if (findings.Count >= MaxFindings) { truncated = true; break; }
                }
            }
        }

        // Active findings first, most severe first; suppressed ones trail.
        findings.Sort((a, b) => a.Suppressed != b.Suppressed
            ? a.Suppressed.CompareTo(b.Suppressed)
            : b.Severity.CompareTo(a.Severity));
        return new ScanResult(filesScanned, findings, filesSkipped, filesIgnored, suppressed, truncated);
    }

    // --- suppression -----------------------------------------------------------

    [GeneratedRegex(@"cerberus:ignore(?:[ \t]+(?<rules>[A-Za-z0-9_.\-]+(?:[ \t]*,[ \t]*[A-Za-z0-9_.\-]+)*))?", RegexOptions.IgnoreCase)]
    private static partial Regex SuppressionPattern();

    /// <summary>
    /// A <c>cerberus:ignore</c> marker suppresses the findings on its own line,
    /// or — when it is alone in a comment — on the line below. Followed by rule
    /// ids (<c>cerberus:ignore crypto.weak-hash, secret.jwt</c>) it suppresses
    /// only those rules; a rule id may be a prefix such as <c>secret</c>.
    /// </summary>
    private static bool IsSuppressed(string ruleId, string line, bool commentOnly = false)
    {
        var match = SuppressionPattern().Match(line);
        if (!match.Success) return false;

        if (commentOnly)
        {
            // Only a line that is nothing but a comment applies to the next line.
            var trimmed = line.TrimStart();
            if (!(trimmed.StartsWith("//") || trimmed.StartsWith('#') || trimmed.StartsWith('%') ||
                  trimmed.StartsWith("--") || trimmed.StartsWith("/*") || trimmed.StartsWith('*') ||
                  trimmed.StartsWith("<!--") || trimmed.StartsWith(';')))
            {
                return false;
            }
        }

        var rules = match.Groups["rules"];
        if (!rules.Success) return true;   // bare marker: every rule
        return rules.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(r => ruleId.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                      ruleId.StartsWith(r + ".", StringComparison.OrdinalIgnoreCase));
    }

    // --- file selection --------------------------------------------------------

    private static bool LooksScannable(string file)
    {
        var name = Path.GetFileName(file);
        if (SkipFileNames.Contains(name)) return false;
        if (SkipExtensions.Contains(Path.GetExtension(name))) return false;
        if (name.EndsWith(".min.js", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".min.css", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        try
        {
            return new FileInfo(file).Length <= MaxFileBytes;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>A NUL byte in the first 8 KB marks a file as binary.</summary>
    private static bool IsBinary(byte[] bytes) =>
        bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0);

    private static readonly string[] TestSegments =
        ["test", "tests", "spec", "specs", "__tests__", "testdata", "test_data", "fixtures", "mocks", "e2e"];

    private static readonly string[] DocSegments =
        ["docs", "doc", "documentation", "examples", "example", "samples", "sample", "demo", "demos"];

    private static readonly string[] DocExtensions = [".md", ".mdx", ".rst", ".txt", ".adoc"];

    /// <summary>"test" for test code, "docs" for documentation and examples, else null.</summary>
    internal static string? ContextOf(string relativePath)
    {
        var segments = relativePath.Split('/');
        var name = segments[^1];
        var stem = Path.GetFileNameWithoutExtension(name);
        var directories = segments.AsSpan(0, segments.Length - 1);

        foreach (var segment in directories)
        {
            if (TestSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)) return "test";
        }
        if (stem.EndsWith("_test", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith("_tests", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith("_SUITE", StringComparison.Ordinal) ||          // Erlang common_test
            stem.EndsWith("Test", StringComparison.Ordinal) ||
            stem.EndsWith("Tests", StringComparison.Ordinal) ||
            stem.StartsWith("test_", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith(".test", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith(".spec", StringComparison.OrdinalIgnoreCase))
        {
            return "test";
        }

        if (DocExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)) return "docs";
        foreach (var segment in directories)
        {
            if (DocSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)) return "docs";
        }
        return null;
    }

    private static IEnumerable<string> EnumerateFiles(string target)
    {
        if (File.Exists(target))
        {
            yield return target;
            yield break;
        }
        if (!Directory.Exists(target)) yield break;

        var stack = new Stack<string>();
        stack.Push(target);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subDirs;
            string[] files;
            try
            {
                subDirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            // Deterministic order, so the same tree always yields the same report.
            Array.Sort(subDirs, StringComparer.Ordinal);
            Array.Sort(files, StringComparer.Ordinal);

            for (var i = subDirs.Length - 1; i >= 0; i--)
            {
                var sub = subDirs[i];
                // Never follow links out of the tree being scanned.
                if (SkipDirs.Contains(Path.GetFileName(sub)) || IsLink(sub)) continue;
                stack.Push(sub);
            }
            foreach (var file in files)
            {
                if (!IsLink(file)) yield return file;
            }
        }
    }

    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

/// <summary>
/// Paths excluded from a scan by <c>.cerberusignore</c> files — one at the
/// scan root and, if different, one in the directory being scanned. One
/// pattern per line; <c>#</c> starts a comment. A pattern is a path relative
/// to the file's own directory: a directory (everything beneath it), a file,
/// or a glob where <c>*</c> matches within a path segment and <c>**</c> across
/// segments. As in .gitignore, a pattern without a slash (<c>*.snap</c>,
/// <c>generated/</c>) matches at any depth, while one with a slash is anchored
/// to the ignore file's directory. Ignore files deeper in the tree are not
/// read. Ignored files are counted in the result, never silently dropped.
/// </summary>
internal sealed class IgnoreList
{
    private readonly List<Regex> _patterns = [];

    public static IgnoreList Empty { get; } = new();

    public static IgnoreList Load(string root, string target)
    {
        var list = new IgnoreList();
        list.AddFile(root, root);
        if (Directory.Exists(target) && !target.Equals(root, StringComparison.Ordinal))
        {
            list.AddFile(root, target);
        }
        return list;
    }

    public bool Matches(string relativePath) => _patterns.Exists(p => p.IsMatch(relativePath));

    private void AddFile(string root, string directory)
    {
        var file = Path.Combine(directory, ScanService.IgnoreFileName);
        string[] lines;
        try
        {
            if (!File.Exists(file)) return;
            lines = File.ReadAllLines(file);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        // Patterns are relative to the ignore file's directory; paths are matched relative to the scan root.
        var prefix = Path.GetRelativePath(root, directory).Replace('\\', '/');
        prefix = prefix == "." ? string.Empty : prefix + "/";

        foreach (var raw in lines)
        {
            var pattern = raw.Trim();
            if (pattern.Length == 0 || pattern.StartsWith('#')) continue;
            pattern = pattern.Replace('\\', '/').TrimStart('/');
            if (pattern.Length == 0 || pattern.Split('/').Contains("..")) continue;   // cannot reach outside
            _patterns.Add(ToRegex(prefix, pattern));
        }
    }

    private static Regex ToRegex(string prefix, string pattern)
    {
        var directoryOnly = pattern.EndsWith('/');
        pattern = pattern.TrimEnd('/');

        // No slash: a name that may sit at any depth below the ignore file.
        var regex = new StringBuilder("^").Append(Regex.Escape(prefix));
        if (!pattern.Contains('/')) regex.Append("(?:.*/)?");

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                regex.Append(".*");
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '/') i++;   // "**/" also matches no directory
            }
            else if (c == '*') regex.Append("[^/]*");
            else if (c == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(c.ToString()));
        }
        // A pattern names a file, or a directory and everything beneath it.
        regex.Append(directoryOnly ? "/.*$" : "(?:/.*)?$");
        return new Regex(regex.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }
}

public sealed record ScanOptions(string ScanRoot);
