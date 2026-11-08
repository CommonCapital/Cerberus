using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cerberus.Scanning;

/// <summary>
/// One resolved dependency discovered in a manifest/lockfile.
/// <paramref name="Ecosystem"/> uses OSV.dev ecosystem names
/// (npm, PyPI, Go, NuGet). <paramref name="Line"/> is 1 when the source
/// format does not carry a usable line (e.g. JSON lockfiles).
/// </summary>
public sealed record Dependency(
    string Ecosystem,
    string Name,
    string Version,
    string File,
    int Line);

/// <summary>
/// Discovers dependency manifests under a target directory and parses them
/// into a normalized dependency list for SCA (software-composition analysis).
/// Lockfiles are preferred over range-only manifests because OSV needs exact
/// versions. Best-effort and language-agnostic; unknown formats are skipped.
/// </summary>
public static class Dependencies
{
    private const int MaxManifestBytes = 5_000_000;

    private static readonly HashSet<string> SkipDirs =
        new(StringComparer.OrdinalIgnoreCase)
        { "node_modules", ".git", "bin", "obj", "dist", ".vs", "packages", "vendor", ".venv", "venv" };

    /// <summary>Walk <paramref name="target"/> and return every dependency found, with the manifest files visited.</summary>
    public static (List<Dependency> Dependencies, List<string> Manifests) Discover(string target, string root)
    {
        var deps = new List<Dependency>();
        var manifests = new List<string>();

        foreach (var file in EnumerateManifests(target))
        {
            var name = Path.GetFileName(file);
            string text;
            try
            {
                if (new FileInfo(file).Length > MaxManifestBytes) continue;
                text = File.ReadAllText(file);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            var rel = Path.GetRelativePath(root, file);
            var before = deps.Count;

            switch (name.ToLowerInvariant())
            {
                case "package-lock.json": ParseNpmLock(text, rel, deps); break;
                case "requirements.txt": ParseRequirements(text, rel, deps); break;
                case "go.mod": ParseGoMod(text, rel, deps); break;
                default:
                    if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                        ParseCsproj(text, rel, deps);
                    break;
            }

            if (deps.Count > before) manifests.Add(rel);
        }

        // De-duplicate identical (ecosystem, name, version) across manifests,
        // keeping the first occurrence (and its file/line).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<Dependency>();
        foreach (var d in deps)
        {
            if (seen.Add($"{d.Ecosystem}|{d.Name}|{d.Version}")) unique.Add(d);
        }
        return (unique, manifests);
    }

    private static IEnumerable<string> EnumerateManifests(string target)
    {
        if (File.Exists(target)) { yield return target; yield break; }
        if (!Directory.Exists(target)) yield break;

        var stack = new Stack<string>();
        stack.Push(target);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] subDirs, files;
            try { subDirs = Directory.GetDirectories(dir); files = Directory.GetFiles(dir); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var sub in subDirs)
                if (!SkipDirs.Contains(Path.GetFileName(sub))) stack.Push(sub);

            foreach (var file in files)
            {
                var n = Path.GetFileName(file);
                if (n.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("go.mod", StringComparison.OrdinalIgnoreCase) ||
                    n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }

    // --- npm: package-lock.json (v1 "dependencies" and v2/v3 "packages") -----
    private static void ParseNpmLock(string text, string file, List<Dependency> deps)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); } catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            // v2/v3: "packages": { "node_modules/x": { "version": "1.2.3" }, "": {root} }
            if (root.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in packages.EnumerateObject())
                {
                    var key = entry.Name;
                    var idx = key.LastIndexOf("node_modules/", StringComparison.Ordinal);
                    if (idx < 0) continue; // skip the root "" entry
                    var pkg = key[(idx + "node_modules/".Length)..];
                    if (string.IsNullOrEmpty(pkg)) continue;
                    if (entry.Value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                        AddDep(deps, "npm", pkg, v.GetString()!, file, 1);
                }
            }

            // v1: "dependencies": { "x": { "version": "1.2.3", "dependencies": {...} } }
            if (deps.Count == 0 && root.TryGetProperty("dependencies", out var d) && d.ValueKind == JsonValueKind.Object)
                WalkNpmV1(d, file, deps);
        }
    }

    private static void WalkNpmV1(JsonElement node, string file, List<Dependency> deps)
    {
        foreach (var entry in node.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (entry.Value.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                AddDep(deps, "npm", entry.Name, v.GetString()!, file, 1);
            if (entry.Value.TryGetProperty("dependencies", out var nested) && nested.ValueKind == JsonValueKind.Object)
                WalkNpmV1(nested, file, deps);
        }
    }

    // --- pip: requirements.txt (pinned "name==version" only) -----------------
    private static readonly Regex Req =
        new(@"^\s*([A-Za-z0-9._-]+)\s*==\s*([A-Za-z0-9._!+-]+)", RegexOptions.Compiled);

    private static void ParseRequirements(string text, string file, List<Dependency> deps)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("-")) continue;
            var m = Req.Match(line);
            if (m.Success) AddDep(deps, "PyPI", m.Groups[1].Value, m.Groups[2].Value, file, i + 1);
        }
    }

    // --- go: go.mod (require blocks and single-line requires) ----------------
    private static readonly Regex GoReq =
        new(@"^\s*([^\s]+/[^\s]+)\s+(v[0-9][^\s]*)", RegexOptions.Compiled);

    private static void ParseGoMod(string text, string file, List<Dependency> deps)
    {
        var lines = text.Split('\n');
        var inBlock = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();
            if (line.StartsWith("require (")) { inBlock = true; continue; }
            if (inBlock && line.StartsWith(')')) { inBlock = false; continue; }

            var candidate = line.StartsWith("require ") ? line["require ".Length..] : (inBlock ? line : null);
            if (candidate is null) continue;

            // Strip trailing // indirect comments.
            var comment = candidate.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) candidate = candidate[..comment];

            var m = GoReq.Match(candidate);
            if (m.Success)
            {
                // OSV Go versions are the bare semver without a leading "v".
                var ver = m.Groups[2].Value.TrimStart('v');
                AddDep(deps, "Go", m.Groups[1].Value, ver, file, i + 1);
            }
        }
    }

    // --- nuget: *.csproj (<PackageReference Include=".." Version=".." />) -----
    private static readonly Regex PkgRef = new(
        @"<PackageReference\s+Include\s*=\s*""([^""]+)""[^>]*?Version\s*=\s*""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void ParseCsproj(string text, string file, List<Dependency> deps)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var m = PkgRef.Match(lines[i]);
            if (m.Success) AddDep(deps, "NuGet", m.Groups[1].Value, m.Groups[2].Value, file, i + 1);
        }
    }

    private static void AddDep(List<Dependency> deps, string eco, string name, string version, string file, int line)
    {
        name = name.Trim();
        version = version.Trim();
        if (name.Length == 0 || version.Length == 0) return;
        deps.Add(new Dependency(eco, name, version, file, line));
    }
}
