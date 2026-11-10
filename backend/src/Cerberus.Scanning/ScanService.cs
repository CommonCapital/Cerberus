namespace Cerberus.Scanning;

public sealed record ScanResult(int FilesScanned, IReadOnlyList<Finding> Findings);

/// <summary>
/// Phase-1 passive scanner: walks a target directory inside the configured
/// scan root, applies the regex rule set per line, and returns normalized
/// findings. Trusted-target MVP — real engagements run this inside the sandbox
/// (see PLAN.md M3/M5). Paths are confined to the scan root.
/// </summary>
public sealed class ScanService(ScanOptions options)
{
    private static readonly HashSet<string> SkipDirs =
        new(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", "bin", "obj", "dist", ".vs", "packages" };

    private const int MaxFileBytes = 1_000_000;
    private const int MaxFindings = 1000;

    public ScanResult Scan(string relativePath, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(options.ScanRoot);
        var target = Path.GetFullPath(Path.Combine(root, relativePath));

        // Confinement: the resolved target must stay within the scan root.
        if (!target.Equals(root, StringComparison.Ordinal) &&
            !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Path is outside the scan root.");
        }

        var findings = new List<Finding>();
        var filesScanned = 0;

        foreach (var file in EnumerateFiles(target))
        {
            ct.ThrowIfCancellationRequested();   // cooperative stop between files
            if (findings.Count >= MaxFindings) break;

            string[] lines;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxFileBytes) continue;
                lines = File.ReadAllLines(file);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            filesScanned++;
            var relFile = Path.GetRelativePath(root, file);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                foreach (var rule in Rules.All)
                {
                    if (!rule.Pattern.IsMatch(line)) continue;

                    findings.Add(new Finding(
                        Id: $"{rule.Id}:{relFile}:{i + 1}",
                        Title: rule.Title,
                        Severity: rule.Severity,
                        Category: rule.Category,
                        Cwe: rule.Cwe,
                        File: relFile,
                        Line: i + 1,
                        Evidence: Truncate(line.Trim(), 200),
                        Confidence: rule.Confidence));

                    if (findings.Count >= MaxFindings) break;
                }
                if (findings.Count >= MaxFindings) break;
            }
        }

        findings.Sort((a, b) => b.Severity.CompareTo(a.Severity));
        return new ScanResult(filesScanned, findings);
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

            foreach (var sub in subDirs)
            {
                if (!SkipDirs.Contains(Path.GetFileName(sub))) stack.Push(sub);
            }
            foreach (var file in files) yield return file;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

public sealed record ScanOptions(string ScanRoot);
