using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerberus.Core.Models;

namespace Cerberus.Api.Agents;

public sealed record Definition(int Line, string Kind, string Name);

/// <summary>
/// A map of the code under review, built without a model: which file imports
/// which, what each file defines, and where requests come in. It lets an agent
/// find entry points and judge how far a problem in one file reaches, at a
/// fraction of the cost of reading files to find out. Pattern-based, so it is
/// a guide and not a proof: imports it cannot resolve are simply not in it.
/// </summary>
public sealed partial class CodeMap
{
    public const int MaxFiles = 3000;
    public const int MaxDefinitions = 300;

    private static readonly string[] JsExtensions = [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"];

    private readonly Dictionary<string, List<string>> _imports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _importedBy = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Definition>> _routes = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Files => _imports.Keys;

    /// <summary>True when the target had more source files than are mapped.</summary>
    public bool Truncated { get; private set; }

    public static bool IsSource(string file) => Language(file) is not null;

    private static string? Language(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" => "js",
        ".py" => "python",
        ".java" or ".kt" => "java",
        ".go" => "go",
        ".rb" => "ruby",
        ".cs" => "csharp",
        ".php" => "php",
        _ => null,
    };

    public static CodeMap Build(CodeWorkspace workspace, CancellationToken ct)
    {
        var map = new CodeMap();
        var sources = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var full in workspace.Files(null))
        {
            ct.ThrowIfCancellationRequested();
            var file = workspace.Relative(full);
            if (!IsSource(file)) continue;
            if (sources.Count >= MaxFiles)
            {
                map.Truncated = true;
                break;
            }
            try
            {
                sources[file] = workspace.ReadLines(file);
            }
            catch (AgentToolException)
            {
                // Binary or unreadable: not part of the map.
            }
        }

        var known = sources.Keys.ToHashSet(StringComparer.Ordinal);
        var byName = known.GroupBy(f => f[(f.LastIndexOf('/') + 1)..]).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var byDirectory = known.GroupBy(DirectoryOf).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var (file, lines) in sources)
        {
            var targets = new List<string>();
            foreach (var specifier in ImportSpecifiers(file, lines))
            {
                foreach (var target in Resolve(file, specifier, known, byName, byDirectory))
                {
                    if (target != file && !targets.Contains(target)) targets.Add(target);
                }
            }
            map._imports[file] = targets;
            foreach (var target in targets)
            {
                if (!map._importedBy.TryGetValue(target, out var list)) map._importedBy[target] = list = [];
                list.Add(file);
            }
            var routes = Outline(file, lines).Where(d => d.Kind == "route").ToList();
            if (routes.Count > 0) map._routes[file] = routes;
        }
        return map;
    }

    public IReadOnlyList<string> ImportsOf(string file) => _imports.TryGetValue(file, out var list) ? list : [];

    public IReadOnlyList<string> ImportedBy(string file) => _importedBy.TryGetValue(file, out var list) ? list : [];

    /// <summary>Every file that depends on this one, directly or through others; nearest first.</summary>
    public IReadOnlyList<string> Dependents(string file, int limit = 200)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { file };
        var queue = new Queue<string>([file]);
        var result = new List<string>();
        while (queue.Count > 0 && result.Count < limit)
        {
            foreach (var importer in ImportedBy(queue.Dequeue()))
            {
                if (!seen.Add(importer)) continue;
                result.Add(importer);
                queue.Enqueue(importer);
                if (result.Count >= limit) break;
            }
        }
        return result;
    }

    /// <summary>
    /// Files nothing else imports but which import others: where execution is
    /// likely to start. Test files start nothing but tests and are left out
    /// unless asked for.
    /// </summary>
    public IReadOnlyList<string> EntryPoints(bool tests = false) =>
        _imports.Where(kv => kv.Value.Count > 0 && ImportedBy(kv.Key).Count == 0 && IsTest(kv.Key) == tests)
            .Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();

    /// <summary>Whether any import was resolved at all. Not so for C#, where a "using" names a namespace, not a file.</summary>
    public bool HasImports => _importedBy.Count > 0;

    public static bool IsTest(string file) => TestFile().IsMatch(file);

    [GeneratedRegex(@"(^|/)(tests?|__tests__|spec|specs)/|\.(test|spec)\.[a-z]+$|_test\.(go|py)$|(^|/)test_[^/]+\.py$|Tests?\.(cs|java|kt)$", RegexOptions.IgnoreCase)]
    private static partial Regex TestFile();

    /// <summary>The files most others depend on, with how many depend on them directly.</summary>
    public IReadOnlyList<(string File, int Importers)> MostDependedOn(int count = 15) =>
        _importedBy.Select(kv => (kv.Key, kv.Value.Count)).OrderByDescending(x => x.Count).ThenBy(x => x.Key, StringComparer.Ordinal).Take(count).ToList();

    public IReadOnlyList<(string File, Definition Route)> Routes() =>
        _routes.OrderBy(kv => kv.Key, StringComparer.Ordinal).SelectMany(kv => kv.Value.Select(r => (kv.Key, r))).ToList();

    // --- what a file imports ---------------------------------------------------

    public static IEnumerable<string> ImportSpecifiers(string file, string[] lines)
    {
        var language = Language(file);
        var inGoBlock = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            switch (language)
            {
                case "js":
                    foreach (Match m in JsImport().Matches(line)) yield return m.Groups[1].Value;
                    break;
                case "python":
                    if (PyFrom().Match(line) is { Success: true } from)
                    {
                        var module = from.Groups[1].Value;
                        yield return module;
                        // "from pkg import name": name may be a module of the package.
                        foreach (var name in from.Groups[2].Value.Split(',').Select(n => n.Trim().Split(' ')[0].Trim('(', ')')).Where(n => n.Length > 0 && n != "*"))
                        {
                            yield return module.EndsWith('.') ? module + name : module + "." + name;
                        }
                    }
                    else if (PyImport().Match(line) is { Success: true } import)
                    {
                        foreach (var module in import.Groups[1].Value.Split(',')) yield return module.Trim().Split(' ')[0];
                    }
                    break;
                case "java":
                    if (JavaImport().Match(line) is { Success: true } java) yield return java.Groups[1].Value;
                    break;
                case "go":
                    if (line.StartsWith("import (", StringComparison.Ordinal)) inGoBlock = true;
                    else if (inGoBlock && line.StartsWith(')')) inGoBlock = false;
                    else if ((inGoBlock || line.StartsWith("import ", StringComparison.Ordinal)) && GoImport().Match(line) is { Success: true } go) yield return go.Groups[1].Value;
                    break;
                case "ruby":
                    if (RubyRequire().Match(line) is { Success: true } ruby) yield return ruby.Groups[1].Value;
                    break;
            }
        }
    }

    private static string DirectoryOf(string file) => file.Contains('/') ? file[..file.LastIndexOf('/')] : "";

    private static string Join(string directory, string relative)
    {
        var parts = new List<string>(directory.Length == 0 ? [] : directory.Split('/'));
        foreach (var part in relative.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return "\0outside";   // leaves the target: resolves to nothing
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static IEnumerable<string> Resolve(
        string file, string specifier, HashSet<string> known, Dictionary<string, List<string>> byName, Dictionary<string, List<string>> byDirectory)
    {
        var directory = DirectoryOf(file);
        switch (Language(file))
        {
            case "js":
            {
                if (!specifier.StartsWith('.')) yield break;   // a package, not a file of this code
                var basePath = Join(directory, specifier);
                // "./x.js" in TypeScript sources means x.ts.
                var stem = JsExtensions.FirstOrDefault(e => basePath.EndsWith(e, StringComparison.Ordinal)) is { } ext ? basePath[..^ext.Length] : basePath;
                foreach (var candidate in new[] { basePath }.Concat(JsExtensions.Select(e => stem + e)).Concat(JsExtensions.Select(e => $"{basePath}/index{e}")))
                {
                    if (known.Contains(candidate))
                    {
                        yield return candidate;
                        yield break;
                    }
                }
                break;
            }
            case "python":
            {
                var dots = specifier.TakeWhile(c => c == '.').Count();
                var path = specifier[dots..].Replace('.', '/');
                var roots = new List<string>();
                if (dots > 0)
                {
                    var from = directory;
                    for (var i = 1; i < dots; i++) from = DirectoryOf(from);
                    roots.Add(from);
                }
                else
                {
                    // An absolute module: the package may be rooted at any directory above the file.
                    for (var dir = directory; ; dir = DirectoryOf(dir))
                    {
                        roots.Add(dir);
                        if (dir.Length == 0) break;
                    }
                }
                foreach (var root in roots)
                {
                    var basePath = path.Length == 0 ? root : (root.Length == 0 ? path : $"{root}/{path}");
                    foreach (var candidate in new[] { $"{basePath}.py", $"{basePath}/__init__.py" })
                    {
                        if (known.Contains(candidate))
                        {
                            yield return candidate;
                            yield break;
                        }
                    }
                }
                break;
            }
            case "java":
            {
                // a.b.C → …/a/b/C.java (or .kt), wherever the source root is.
                var path = specifier.Replace('.', '/');
                var name = path[(path.LastIndexOf('/') + 1)..];
                foreach (var extension in new[] { ".java", ".kt" })
                {
                    if (byName.TryGetValue(name + extension, out var candidates) &&
                        candidates.FirstOrDefault(c => c == path + extension || c.EndsWith("/" + path + extension, StringComparison.Ordinal)) is { } match)
                    {
                        yield return match;
                        yield break;
                    }
                }
                break;
            }
            case "go":
            {
                // "module/path/pkg" → the directory of this code whose path ends the import path.
                var match = byDirectory.Keys
                    .Where(d => d.Length > 0 && (specifier == d || specifier.EndsWith("/" + d, StringComparison.Ordinal)))
                    .OrderByDescending(d => d.Length).FirstOrDefault();
                if (match is null) yield break;
                foreach (var target in byDirectory[match].Where(f => f.EndsWith(".go", StringComparison.Ordinal) && !f.EndsWith("_test.go", StringComparison.Ordinal)))
                {
                    yield return target;
                }
                break;
            }
            case "ruby":
            {
                var candidate = Join(directory, specifier.EndsWith(".rb", StringComparison.Ordinal) ? specifier : specifier + ".rb");
                if (known.Contains(candidate)) yield return candidate;
                break;
            }
        }
    }

    // --- what a file defines ---------------------------------------------------

    /// <summary>The functions, classes, types and request routes a file defines, with their lines.</summary>
    public static IReadOnlyList<Definition> Outline(string file, string[] lines)
    {
        var language = Language(file);
        var found = new List<Definition>();
        if (language is null) return found;

        for (var i = 0; i < lines.Length && found.Count < MaxDefinitions; i++)
        {
            var line = lines[i];
            if (line.Length > 400) continue;
            var number = i + 1;

            foreach (Match route in RoutePattern(language).Matches(line))
            {
                var method = route.Groups["method"].Success ? route.Groups["method"].Value.ToUpperInvariant() : "ANY";
                if (method is "USE" or "ALL" or "ROUTE" or "HANDLE" or "HANDLEFUNC" or "REQUEST" or "MAP") method = "ANY";
                found.Add(new Definition(number, "route", $"{method} {route.Groups["path"].Value}"));
            }

            Match m;
            switch (language)
            {
                case "python":
                    if ((m = PyDefinition().Match(line)).Success)
                    {
                        var isClass = m.Groups[2].Value == "class";
                        found.Add(new Definition(number, isClass ? "class" : m.Groups[1].Length > 0 ? "method" : "function", m.Groups[3].Value));
                    }
                    break;
                case "js":
                    if ((m = JsFunction().Match(line)).Success) found.Add(new Definition(number, "function", m.Groups[1].Value));
                    else if ((m = JsClass().Match(line)).Success) found.Add(new Definition(number, "class", m.Groups[1].Value));
                    else if ((m = JsArrow().Match(line)).Success) found.Add(new Definition(number, "function", m.Groups[1].Value));
                    else if ((m = JsType().Match(line)).Success) found.Add(new Definition(number, m.Groups[1].Value, m.Groups[2].Value));
                    break;
                case "csharp":
                case "java":
                    if ((m = TypeDeclaration().Match(line)).Success) found.Add(new Definition(number, m.Groups[1].Value, m.Groups[2].Value));
                    else if ((m = MethodDeclaration().Match(line)).Success && !Keywords.Contains(m.Groups[1].Value)) found.Add(new Definition(number, "method", m.Groups[1].Value));
                    else if (language == "java" && (m = KotlinFun().Match(line)).Success) found.Add(new Definition(number, "function", m.Groups[1].Value));
                    break;
                case "go":
                    if ((m = GoFunc().Match(line)).Success) found.Add(new Definition(number, m.Groups[1].Success ? "method" : "function", m.Groups[2].Value));
                    else if ((m = GoType().Match(line)).Success) found.Add(new Definition(number, m.Groups[2].Value, m.Groups[1].Value));
                    break;
                case "ruby":
                    if ((m = RubyDefinition().Match(line)).Success) found.Add(new Definition(number, m.Groups[1].Value == "def" ? "function" : m.Groups[1].Value, m.Groups[2].Value));
                    break;
                case "php":
                    if ((m = PhpDefinition().Match(line)).Success) found.Add(new Definition(number, m.Groups[1].Value == "function" ? "function" : "class", m.Groups[2].Value));
                    break;
            }
        }
        return found;
    }

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "new", "throw", "await", "nameof", "typeof",
    };

    private static Regex RoutePattern(string language) => language switch
    {
        "js" => JsRoute(),
        "python" => PyRoute(),
        "csharp" => CsRoute(),
        "java" => JavaRoute(),
        "go" => GoRoute(),
        _ => NoRoute(),
    };

    [GeneratedRegex("""(?:\bfrom\s*|\bimport\s*\(?\s*|\brequire\s*\(\s*)['"]([^'"]+)['"]""")]
    private static partial Regex JsImport();

    [GeneratedRegex(@"^from\s+([\.\w]+)\s+import\s+(.+)$")]
    private static partial Regex PyFrom();

    [GeneratedRegex(@"^import\s+([\w\.,\s]+?)(?:\s*#.*)?$")]
    private static partial Regex PyImport();

    [GeneratedRegex(@"^import\s+(?:static\s+)?([\w\.]+)\s*;?")]
    private static partial Regex JavaImport();

    [GeneratedRegex("\"([^\"]+)\"")]
    private static partial Regex GoImport();

    [GeneratedRegex("""^require_relative\s+['"]([^'"]+)['"]""")]
    private static partial Regex RubyRequire();

    [GeneratedRegex(@"^(\s*)(?:async\s+)?(def|class)\s+(\w+)")]
    private static partial Regex PyDefinition();

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s*(\w+)")]
    private static partial Regex JsFunction();

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:default\s+)?(?:abstract\s+)?class\s+(\w+)")]
    private static partial Regex JsClass();

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:const|let|var)\s+(\w+)\s*(?::[^=]+)?=\s*(?:async\s*)?(?:\([^)]*\)|\w+)\s*(?::[^=]+)?=>")]
    private static partial Regex JsArrow();

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:declare\s+)?(interface|type|enum)\s+(\w+)")]
    private static partial Regex JsType();

    [GeneratedRegex(@"^\s*(?:(?:public|internal|private|protected|static|abstract|sealed|final|partial|open|data)\s+)*(class|interface|record|struct|enum)\s+(\w+)")]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"^\s*(?:(?:public|internal|private|protected|static|async|virtual|override|abstract|sealed|final|synchronized)\s+)+[\w<>\[\],\.\?\s]+?\s+(\w+)\s*\(")]
    private static partial Regex MethodDeclaration();

    [GeneratedRegex(@"^\s*(?:(?:public|private|internal|protected|override|suspend|open)\s+)*fun\s+(?:<[^>]+>\s*)?(?:[\w\.]+\.)?(\w+)\s*\(")]
    private static partial Regex KotlinFun();

    [GeneratedRegex(@"^func\s+(\([^)]*\)\s*)?(\w+)\s*\(")]
    private static partial Regex GoFunc();

    [GeneratedRegex(@"^type\s+(\w+)\s+(struct|interface)")]
    private static partial Regex GoType();

    [GeneratedRegex(@"^\s*(def|class|module)\s+([\w\.:\?!]+)")]
    private static partial Regex RubyDefinition();

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|static|abstract|final)\s+)*(function|class|interface|trait)\s+(\w+)")]
    private static partial Regex PhpDefinition();

    [GeneratedRegex("""\b(?:app|router|server|api|route|routes)\.(?<method>get|post|put|patch|delete|all|use)\(\s*['"`](?<path>/[^'"`]*)['"`]""", RegexOptions.IgnoreCase)]
    private static partial Regex JsRoute();

    [GeneratedRegex("""@\w+(?:\.\w+)*\.(?<method>get|post|put|patch|delete|route)\(\s*['"](?<path>/[^'"]*)['"]""", RegexOptions.IgnoreCase)]
    private static partial Regex PyRoute();

    [GeneratedRegex("""(?:\.Map(?<method>Get|Post|Put|Patch|Delete)\(\s*"(?<path>[^"]*)"|\[Http(?<method>Get|Post|Put|Patch|Delete)\(\s*"(?<path>[^"]*)")""")]
    private static partial Regex CsRoute();

    [GeneratedRegex("""@(?<method>Get|Post|Put|Patch|Delete|Request)Mapping\(\s*(?:value\s*=\s*|path\s*=\s*)?"(?<path>[^"]*)""")]
    private static partial Regex JavaRoute();

    [GeneratedRegex("""\.(?<method>HandleFunc|Handle|GET|POST|PUT|PATCH|DELETE)\(\s*"(?<path>/[^"]*)""")]
    private static partial Regex GoRoute();

    [GeneratedRegex("(?!)")]
    private static partial Regex NoRoute();
}

/// <summary>The code map as tools: an overview, a file's outline, and who depends on a file.</summary>
public static class CodeMapTools
{
    public const string Overview = "codeMap";
    public const string Outline = "outline";
    public const string Dependents = "dependents";

    public static IReadOnlyList<IAgentTool> For(CodeWorkspace workspace)
    {
        // Built on first use and shared by the three tools: one pass over the target per run.
        var map = new Lazy<CodeMap>(() => CodeMap.Build(workspace, CancellationToken.None));
        return [new OverviewTool(map), new OutlineTool(workspace, map), new DependentsTool(workspace, map)];
    }

    private const string Caveat = "Built from patterns in the source, without running it: a guide, not a proof. Imports it could not resolve to a file are not shown.";

    private sealed class OverviewTool(Lazy<CodeMap> map) : IAgentTool
    {
        public ToolDefinition Definition { get; } = CodeTools.Define(Overview,
            "A map of the code under review: where requests come in (routes), which files start execution (entry points), and which files most others depend on. Call it once, early, to decide what to read.",
            """{"type":"object","properties":{},"additionalProperties":false}""");

        public Task<string> RunAsync(JsonElement input, CancellationToken ct)
        {
            var code = map.Value;
            var text = new StringBuilder();
            text.AppendLine($"{code.Files.Count} source file(s) mapped{(code.Truncated ? $" (the first {CodeMap.MaxFiles}; there are more)" : "")}. {Caveat}");

            var routes = code.Routes();
            text.AppendLine().AppendLine($"Request routes ({routes.Count}):");
            foreach (var (file, route) in routes.Take(60)) text.AppendLine($"  {route.Name}  — {file}:{route.Line}");
            if (routes.Count > 60) text.AppendLine($"  … {routes.Count - 60} more");
            if (routes.Count == 0) text.AppendLine("  none recognised");

            if (!code.HasImports)
            {
                // E.g. C#: files are tied together by namespaces, which this map does not follow.
                text.AppendLine().AppendLine("No imports between files could be resolved for this code, so there are no entry points or dependents to show. Use the routes above, listDirectory and grep.");
                return Task.FromResult(text.ToString());
            }

            var entries = code.EntryPoints();
            var testEntries = code.EntryPoints(tests: true).Count;
            text.AppendLine().AppendLine($"Entry points — files nothing imports ({entries.Count}{(testEntries > 0 ? $", not counting {testEntries} test file(s)" : "")}):");
            foreach (var file in entries.Take(40)) text.AppendLine($"  {file}");
            if (entries.Count > 40) text.AppendLine($"  … {entries.Count - 40} more");

            text.AppendLine().AppendLine("Most depended on (files importing it directly):");
            foreach (var (file, importers) in code.MostDependedOn()) text.AppendLine($"  {importers,3}  {file}");
            return Task.FromResult(text.ToString());
        }
    }

    private sealed class OutlineTool(CodeWorkspace workspace, Lazy<CodeMap> map) : IAgentTool
    {
        public ToolDefinition Definition { get; } = CodeTools.Define(Outline,
            "What a file defines — functions, classes, types and request routes, with line numbers — and which files it imports. Much cheaper than reading the file; use it to decide which part to read.",
            """{"type":"object","properties":{"path":{"type":"string","description":"File, relative to the code under review"}},"required":["path"],"additionalProperties":false}""");

        public Task<string> RunAsync(JsonElement input, CancellationToken ct)
        {
            var path = CodeTools.Text(input, "path") ?? throw new AgentToolException("path is required.");
            var lines = workspace.ReadLines(path);
            var file = workspace.Relative(workspace.Resolve(path));
            if (!CodeMap.IsSource(file)) throw new AgentToolException("No outline for this kind of file; read it instead.");

            var definitions = CodeMap.Outline(file, lines);
            var text = new StringBuilder();
            text.AppendLine($"{file}: {lines.Length} line(s), {definitions.Count} definition(s){(definitions.Count >= CodeMap.MaxDefinitions ? " (cut off; the file has more)" : "")}.");
            var imports = map.Value.ImportsOf(file);
            if (imports.Count > 0) text.AppendLine($"Imports: {string.Join(", ", imports)}");
            foreach (var definition in definitions) text.AppendLine($"{definition.Line,5}  {definition.Kind} {definition.Name}");
            return Task.FromResult(text.ToString());
        }
    }

    private sealed class DependentsTool(CodeWorkspace workspace, Lazy<CodeMap> map) : IAgentTool
    {
        public ToolDefinition Definition { get; } = CodeTools.Define(Dependents,
            "How far a file reaches: what it imports, which files import it, and everything that depends on it through others. Use it to judge the blast radius of a problem in that file.",
            """{"type":"object","properties":{"path":{"type":"string","description":"File, relative to the code under review"}},"required":["path"],"additionalProperties":false}""");

        public Task<string> RunAsync(JsonElement input, CancellationToken ct)
        {
            var path = CodeTools.Text(input, "path") ?? throw new AgentToolException("path is required.");
            var file = workspace.Relative(workspace.Resolve(path));
            var code = map.Value;
            if (!code.Files.Contains(file)) throw new AgentToolException("That file is not in the code map (not a source file it understands, or not found).");

            var direct = code.ImportedBy(file);
            var all = code.Dependents(file);
            var text = new StringBuilder();
            text.AppendLine($"{file}. {Caveat}");
            text.AppendLine($"Imports ({code.ImportsOf(file).Count}): {List(code.ImportsOf(file))}");
            text.AppendLine($"Imported by ({direct.Count}): {List(direct)}");
            text.AppendLine($"Everything that depends on it, directly or through others ({all.Count}{(all.Count >= 200 ? "+" : "")}): {List(all.Except(direct).ToList(), "nothing further")}");
            return Task.FromResult(text.ToString());
        }

        private static string List(IReadOnlyList<string> files, string none = "nothing") =>
            files.Count == 0 ? none : string.Join(", ", files.Take(60)) + (files.Count > 60 ? $", … {files.Count - 60} more" : "");
    }
}
