using System.Text.Json;
using Cerberus.Api.Agents;

namespace Cerberus.Tests;

/// <summary>A small polyglot project on disk.</summary>
public sealed class MappedCode : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cerberus-map-" + Guid.NewGuid().ToString("n"));

    public MappedCode()
    {
        // TypeScript: index → routes → db; index → db (written the ESM way, with .js)
        Write("web/src/index.ts", "import express from 'express';\nimport { routes } from './routes';\nimport { db } from './db.js';\nconst app = express();\napp.get('/health', (req, res) => res.send('ok'));\n");
        Write("web/src/routes/index.ts", "import { db } from '../db';\nimport { helper } from '../../../outside';\nexport const routes = (router) => {\n  router.post(\"/login\", login);\n};\nexport async function login(req, res) {}\nexport class Session {}\nexport interface User { id: string }\n");
        Write("web/src/db.ts", "export const db = { query: (sql: string) => sql };\nexport type Row = Record<string, unknown>;\n");
        // Python: main → auth → db; main → util (relative)
        Write("api/app/__init__.py", "");
        Write("api/app/main.py", "from app.auth import login\nfrom . import util\nimport os, sys\n\n@app.route(\"/items\")\ndef items():\n    return login()\n");
        Write("api/app/auth.py", "import app.db as database\n\nclass Auth:\n    def check(self):\n        pass\n\nasync def login():\n    pass\n");
        Write("api/app/db.py", "def connect():\n    pass\n");
        Write("api/app/util.py", "X = 1\n");
        // Java: A → util.B, by package path under any source root
        Write("svc/src/main/java/com/acme/A.java", "package com.acme;\nimport com.acme.util.B;\nimport java.util.List;\npublic class A {\n    @GetMapping(\"/a\")\n    public String handle(String id) { return id; }\n}\n");
        Write("svc/src/main/java/com/acme/util/B.java", "package com.acme.util;\npublic final class B {}\n");
        // Go: main → the store package (its test file is not a dependency)
        Write("go/cmd/server/main.go", "package main\n\nimport (\n\t\"fmt\"\n\t\"example.com/proj/go/internal/store\"\n)\n\nfunc main() {\n\tmux.HandleFunc(\"/v1/items\", list)\n}\n");
        Write("go/internal/store/store.go", "package store\n\ntype Store struct{}\n\nfunc (s *Store) Get() {}\n\nfunc Open() *Store { return nil }\n");
        Write("go/internal/store/store_test.go", "package store\n");
        // C#: outlined, not resolved
        Write("dotnet/Api.cs", "namespace X;\npublic sealed class Api\n{\n    public async Task<int> RunAsync(string id)\n    {\n        if (id is null) return 0;\n        app.MapPost(\"/jobs\", Submit);\n        return 1;\n    }\n}\n");
        // A test file: imports the code, is imported by nothing, and is not an entry point of the system.
        Write("web/src/db.test.ts", "import { db } from './db';\n");
        Write("README.md", "# not source\n");
    }

    private void Write(string relative, string text)
    {
        var full = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    public CodeWorkspace Workspace => new(Root, ".", redact: false);

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public class CodeMapTests
{
    private static JsonElement J(object value) => JsonDocument.Parse(JsonSerializer.Serialize(value)).RootElement.Clone();

    private static IAgentTool Tool(CodeWorkspace workspace, string name) => CodeTools.For(workspace).Single(t => t.Definition.Name == name);

    [Fact]
    public void Imports_are_resolved_to_files_of_the_project_in_each_language()
    {
        using var code = new MappedCode();
        var map = CodeMap.Build(code.Workspace, default);

        // TypeScript: a directory import finds its index; "./db.js" means db.ts; a package and a path leaving the target resolve to nothing.
        Assert.Equal(["web/src/routes/index.ts", "web/src/db.ts"], map.ImportsOf("web/src/index.ts"));
        Assert.Equal(["web/src/db.ts"], map.ImportsOf("web/src/routes/index.ts"));
        // Python: a package-absolute import, a relative one, and "import a.b as c"; the standard library is not part of the map.
        Assert.Equal(["api/app/auth.py", "api/app/__init__.py", "api/app/util.py"], map.ImportsOf("api/app/main.py"));
        Assert.Equal(["api/app/db.py"], map.ImportsOf("api/app/auth.py"));
        // Java: by package path, wherever the source root is.
        Assert.Equal(["svc/src/main/java/com/acme/util/B.java"], map.ImportsOf("svc/src/main/java/com/acme/A.java"));
        // Go: the package's files, without its tests.
        Assert.Equal(["go/internal/store/store.go"], map.ImportsOf("go/cmd/server/main.go"));

        Assert.DoesNotContain("README.md", map.Files);
        Assert.False(map.Truncated);
    }

    [Fact]
    public void Dependents_follow_the_imports_backwards_as_far_as_they_go()
    {
        using var code = new MappedCode();
        var map = CodeMap.Build(code.Workspace, default);

        Assert.Equal(["web/src/db.test.ts", "web/src/index.ts", "web/src/routes/index.ts"], map.ImportedBy("web/src/db.ts").Order().ToArray());
        // db.py ← auth.py ← main.py: a problem in db.py reaches main.py through auth.py.
        Assert.Equal(["api/app/auth.py", "api/app/main.py"], map.Dependents("api/app/db.py").ToArray());
        Assert.Empty(map.Dependents("api/app/main.py"));
    }

    [Fact]
    public void Entry_points_central_files_and_routes_are_found()
    {
        using var code = new MappedCode();
        var map = CodeMap.Build(code.Workspace, default);

        Assert.Equal(
            ["api/app/main.py", "go/cmd/server/main.go", "svc/src/main/java/com/acme/A.java", "web/src/index.ts"],
            map.EntryPoints().ToArray());
        Assert.Equal(["web/src/db.test.ts"], map.EntryPoints(tests: true).ToArray());   // listed apart
        Assert.Equal(("web/src/db.ts", 3), map.MostDependedOn(1)[0]);

        var routes = map.Routes().Select(r => $"{r.Route.Name} @ {r.File}:{r.Route.Line}").ToArray();
        Assert.Equal(
        [
            "ANY /items @ api/app/main.py:5",
            "POST /jobs @ dotnet/Api.cs:7",
            "ANY /v1/items @ go/cmd/server/main.go:9",
            "GET /a @ svc/src/main/java/com/acme/A.java:5",
            "GET /health @ web/src/index.ts:5",
            "POST /login @ web/src/routes/index.ts:4",
        ], routes);
    }

    [Fact]
    public void An_outline_lists_what_a_file_defines_with_line_numbers()
    {
        using var code = new MappedCode();
        string[] Outline(string file) => CodeMap.Outline(file, code.Workspace.ReadLines(file)).Where(d => d.Kind != "route").Select(d => $"{d.Line} {d.Kind} {d.Name}").ToArray();

        Assert.Equal(["3 class Auth", "4 method check", "7 function login"], Outline("api/app/auth.py"));
        Assert.Equal(["3 function routes", "6 function login", "7 class Session", "8 interface User"], Outline("web/src/routes/index.ts"));
        Assert.Equal(["2 class Api", "4 method RunAsync"], Outline("dotnet/Api.cs"));   // "if (…)" is not a method
        Assert.Equal(["3 struct Store", "5 method Get", "7 function Open"], Outline("go/internal/store/store.go"));
        Assert.Equal(["4 class A", "6 method handle"], Outline("svc/src/main/java/com/acme/A.java"));
        Assert.Empty(CodeMap.Outline("README.md", ["# title"]));
    }

    [Fact]
    public async Task The_tools_give_an_agent_the_overview_an_outline_and_a_blast_radius()
    {
        using var code = new MappedCode();
        var workspace = code.Workspace;

        var overview = await Tool(workspace, CodeMapTools.Overview).RunAsync(J(new { }), default);
        Assert.Contains("source file(s) mapped", overview);
        Assert.Contains("POST /login  — web/src/routes/index.ts:4", overview);
        Assert.Contains("Entry points", overview);
        Assert.Contains("  3  web/src/db.ts", overview);
        Assert.Contains("not counting 1 test file(s)", overview);
        Assert.DoesNotContain("  web/src/db.test.ts", overview);
        Assert.Contains("a guide, not a proof", overview);

        var outline = await Tool(workspace, CodeMapTools.Outline).RunAsync(J(new { path = "api/app/auth.py" }), default);
        Assert.Contains("api/app/auth.py: 8 line(s), 3 definition(s).", outline);
        Assert.Contains("Imports: api/app/db.py", outline);
        Assert.Contains("    7  function login", outline);

        var reach = await Tool(workspace, CodeMapTools.Dependents).RunAsync(J(new { path = "api/app/db.py" }), default);
        Assert.Contains("Imported by (1): api/app/auth.py", reach);
        Assert.Contains("(2): api/app/main.py", reach);   // reached through auth.py
    }

    [Fact]
    public async Task The_tools_stay_inside_the_target_and_say_so_when_a_file_is_not_mapped()
    {
        using var code = new MappedCode();
        var workspace = new CodeWorkspace(code.Root, "web", redact: false);   // only web/ is under review

        await Assert.ThrowsAsync<AgentToolException>(() => Tool(workspace, CodeMapTools.Outline).RunAsync(J(new { path = "../api/app/auth.py" }), default));
        await Assert.ThrowsAsync<AgentToolException>(() => Tool(workspace, CodeMapTools.Dependents).RunAsync(J(new { path = "src/missing.ts" }), default));
        await Assert.ThrowsAsync<AgentToolException>(() => Tool(workspace, CodeMapTools.Outline).RunAsync(J(new { }), default));

        // The map of a narrower target holds only its own files.
        var overview = await Tool(workspace, CodeMapTools.Overview).RunAsync(J(new { }), default);
        Assert.Contains("4 source file(s) mapped", overview);
        Assert.DoesNotContain("api/app", overview);

        // C# on its own: routes, but no import graph — and it says so instead of showing empty lists.
        var csharp = await Tool(new CodeWorkspace(code.Root, "dotnet", redact: false), CodeMapTools.Overview).RunAsync(J(new { }), default);
        Assert.Contains("POST /jobs  — Api.cs:7", csharp);
        Assert.Contains("No imports between files could be resolved", csharp);
        Assert.DoesNotContain("Entry points", csharp);
    }

    [Theory]
    [InlineData("src/db.test.ts", true)]
    [InlineData("src/__tests__/db.ts", true)]
    [InlineData("pkg/store_test.go", true)]
    [InlineData("app/test_auth.py", true)]
    [InlineData("tests/Cerberus.Tests/AgentTests.cs", true)]
    [InlineData("src/test/java/com/acme/ATest.java", true)]
    [InlineData("src/latest.ts", false)]
    [InlineData("src/contest/index.ts", false)]
    [InlineData("app/attestation.py", false)]
    [InlineData("src/Api.cs", false)]
    public void Test_files_are_told_apart_from_the_code_they_test(string file, bool isTest)
    {
        Assert.Equal(isTest, CodeMap.IsTest(file));
    }

    [Fact]
    public void Every_agent_gets_the_code_map_tools()
    {
        using var code = new MappedCode();
        var names = CodeTools.For(code.Workspace).Select(t => t.Definition.Name).ToArray();
        Assert.Equal(["listDirectory", "readFile", "grep", "codeMap", "outline", "dependents"], names);
    }
}
