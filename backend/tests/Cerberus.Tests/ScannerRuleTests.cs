using Cerberus.Scanning;

namespace Cerberus.Tests;

// This file is full of deliberately insecure sample lines; it is listed in
// .cerberusignore so the platform's self-scan does not report its own tests.

/// <summary>Scans throwaway directories so each test controls exactly what the scanner sees.</summary>
public sealed class ScanSandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cerberus-scan-" + Guid.NewGuid().ToString("n"));

    public ScanSandbox() => Directory.CreateDirectory(Root);

    public ScanSandbox File(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return this;
    }

    public ScanSandbox Bytes(string relativePath, byte[] content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, content);
        return this;
    }

    public ScanResult Scan(string path = ".") => new ScanService(new ScanOptions(Root)).Scan(path);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class ScannerRuleTests
{
    /// <summary>The ids of the rules that fire on a single line in a file of the given name.</summary>
    private static string[] RulesFiring(string line, string fileName = "sample.py")
    {
        using var sandbox = new ScanSandbox().File(fileName, line + "\n");
        return sandbox.Scan().Findings.Select(f => f.Id.Split(':')[0]).Distinct().Order().ToArray();
    }

    // Sample secrets are assembled at runtime so no real-looking token sits in the repository.
    private static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    public static TheoryData<string, string, string> Positives => new()
    {
        // --- secrets
        { "secret.aws-access-key", "sample.py", "key = '" + "AKIA" + Repeat("A", 12) + "ABCD'" },
        { "secret.aws-access-key", "sample.py", "temp = '" + "ASIA" + Repeat("B", 16) + "'" },
        { "secret.private-key", "id_rsa", "-----BEGIN " + "RSA PRIVATE KEY-----" },
        { "secret.private-key", "key.pem", "-----BEGIN " + "PRIVATE KEY-----" },
        { "secret.github-token", "sample.py", "t = '" + "ghp_" + Repeat("a1B2", 9) + "'" },
        { "secret.github-token", "sample.py", "t = '" + "github_pat_" + Repeat("a1B2_", 9) + "'" },
        { "secret.slack-token", "sample.js", "const t = '" + "xoxb-" + "1234567890-abcdefghij'" },
        { "secret.google-api-key", "sample.js", "const k = '" + "AIza" + Repeat("Ab1_", 8) + "xyz'" },
        { "secret.stripe-live-key", "sample.rb", "key = '" + "sk_live_" + Repeat("a1B2", 6) + "'" },
        { "secret.jwt", "sample.js", "const jwt = '" + "eyJ" + Repeat("hbGc", 4) + "." + "eyJ" + Repeat("zdWI", 4) + "." + Repeat("SflK", 4) + "'" },
        { "secret.connection-string", "config.yml", "url: postgres://app:" + "hunter2x" + "@db.internal:5432/prod" },
        { "secret.connection-string", "config.yml", "uri: amqps://svc:" + "Tr0ub4dor" + "@mq.example.com/vhost" },
        { "secret.generic-assignment", "settings.py", "API_KEY = \"" + Repeat("abc123", 3) + "\"" },
        { "secret.generic-assignment", "app.js", "const password = '" + "correcthorsebattery" + "';" },

        // --- crypto
        { "crypto.weak-hash", "sample.py", "digest = hashlib.md5(data).hexdigest()" },
        { "crypto.weak-hash", "sample.php", "$h = sha1($password);" },
        { "crypto.weak-hash", "Sample.java", "MessageDigest md = MessageDigest.getInstance(\"SHA-1\");" },
        { "crypto.weak-hash", "Sample.cs", "using var md5 = MD5.Create();" },
        { "crypto.weak-hash", "sample.js", "crypto.createHash('md5').update(x)" },
        { "crypto.weak-hash", "sample.erl", "Hash = erlang:md5(Bin)," },
        { "crypto.weak-hash", "sample.erl", "Hash = crypto:hash(md5, Bin)," },
        { "crypto.weak-hash", "sample.ex", "hash = :crypto.hash(:sha, data)" },
        { "crypto.weak-cipher", "Sample.java", "Cipher c = Cipher.getInstance(\"AES/ECB/PKCS5Padding\");" },
        { "crypto.weak-cipher", "sample.py", "cipher = AES.new(key, AES.MODE_ECB)" },
        { "crypto.weak-cipher", "Sample.java", "Cipher c = Cipher.getInstance(\"DES/CBC/PKCS5Padding\");" },
        { "crypto.weak-cipher", "sample.erl", "crypto:crypto_one_time(des_ede3, Key, IV, Data, true)" },

        // --- configuration
        { "tls.verification-disabled", "sample.py", "requests.get(url, verify=False)" },
        { "tls.verification-disabled", "sample.js", "new https.Agent({ rejectUnauthorized: false })" },
        { "tls.verification-disabled", "sample.go", "tls.Config{InsecureSkipVerify: true}" },
        { "tls.verification-disabled", "sample.erl", "ssl:connect(Host, Port, [{verify, verify_none}])" },
        { "tls.verification-disabled", "sample.ex", "ssl: [verify: :verify_none]" },
        { "tls.verification-disabled", "Sample.cs", "handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;" },
        { "config.debug-enabled", "settings.py", "DEBUG = True" },
        { "config.debug-enabled", "app.py", "app.run(host='0.0.0.0', debug=True)" },
        { "config.cors-wildcard", "server.js", "res.setHeader('Access-Control-Allow-Origin', '*')" },
        { "config.world-writable", "deploy.sh", "chmod -R 777 /var/app" },
        { "config.world-writable", "setup.py", "os.chmod(path, 0o777)" },

        // --- injection
        { "injection.eval", "sample.js", "const v = eval(userInput);" },
        { "injection.eval", "sample.js", "const f = new Function('return ' + body);" },
        { "injection.eval-beam", "sample.ex", "{value, _} = Code.eval_string(expr)" },
        { "injection.eval-beam", "sample.erl", "{value, V, _} = erl_eval:expr(Expr, Bindings)," },
        { "injection.shell-true", "sample.py", "subprocess.run(cmd, shell=True)" },
        { "injection.os-command", "sample.py", "os.system('ping ' + host)" },
        { "injection.os-command", "sample.erl", "Out = os:cmd(\"ls \" ++ Dir)," },
        { "injection.os-command", "sample.ex", "out = :os.cmd(~c\"ls\")" },
        { "injection.os-command", "sample.js", "child_process.exec('ls ' + dir, cb)" },
        { "injection.sql-concatenation", "sample.py", "cur.execute(\"SELECT * FROM users WHERE id = \" + user_id)" },
        { "injection.sql-concatenation", "sample.js", "db.query('select name from users where id=' + id)" },
        { "injection.sql-concatenation", "sample.py", "cur.execute(\"SELECT name FROM users WHERE id = %s\" % user_id)" },
        { "injection.sql-concatenation", "sample.py", "cur.execute(f\"DELETE FROM sessions WHERE token = '{token}'\")" },

        // --- deserialization
        { "deserialization.unsafe", "sample.py", "obj = pickle.loads(blob)" },
        { "deserialization.unsafe", "sample.py", "cfg = yaml.load(stream)" },
        { "deserialization.unsafe", "Sample.java", "ObjectInputStream in = new ObjectInputStream(socket.getInputStream());" },
        { "deserialization.unsafe", "sample.rb", "obj = Marshal.load(data)" },
        { "deserialization.erlang-term", "sample.erl", "Term = binary_to_term(Bin)," },
        { "deserialization.erlang-term", "sample.ex", "term = :erlang.binary_to_term(bin)" },
    };

    [Theory]
    [MemberData(nameof(Positives))]
    public void Rule_fires_on_its_pattern(string ruleId, string fileName, string line)
    {
        Assert.Contains(ruleId, RulesFiring(line, fileName));
    }

    public static TheoryData<string, string> Negatives => new()
    {
        // Mentions, not uses
        { "notes.py", "# we migrated away from md5 last year" },
        { "sample.erl", "-spec load(Algo :: crypto:sha1() | crypto:sha2()) -> ok." },
        { "sample.py", "hashlib.sha256(data).hexdigest()" },
        { "sample.js", "crypto.createHash('sha256')" },
        // A function that is only *named* eval
        { "filter.erl", "eval(undefined, _Mc) ->" },
        { "filter.erl", "    true = eval(Filters, Mc)," },
        { "sample.py", "def eval(self, node):" },
        { "sample.js", "function eval (x) { return x }" },
        { "sample.py", "result = model.eval()" },
        { "sample.js", "retrieval(x)" },
        // Placeholders and credential-free URLs
        { "README.yml", "url: amqp://user:password@server-name/vhost" },
        { "config.yml", "url: postgres://db.internal:5432/prod" },
        { "config.yml", "url: https://example.com/path@v2" },
        { "config.py", "password = os.environ['DB_PASSWORD']" },
        { "config.py", "password = \"\"" },
        { "config.py", "token = get_token()" },
        // Safe variants
        { "sample.py", "requests.get(url, verify=True)" },
        { "sample.py", "cfg = yaml.load(stream, Loader=yaml.SafeLoader)" },
        { "sample.py", "cfg = yaml.safe_load(stream)" },
        { "sample.erl", "Term = binary_to_term(Bin, [safe])," },
        { "sample.py", "subprocess.run(['ls', path])" },
        { "sample.py", "cur.execute(\"SELECT * FROM users WHERE id = %s\", (user_id,))" },
        { "sample.py", "log.info(\"select the item from the list\" + name)" },
        { "sample.py", "title = \"Update the settings\" + suffix" },
        { "deploy.sh", "chmod 750 /var/app" },
        { "settings.py", "DEBUG = False" },
        { "server.js", "res.setHeader('Access-Control-Allow-Origin', 'https://app.example.com')" },
        { "sample.py", "key = 'AKIA-not-a-key'" },
    };

    [Theory]
    [MemberData(nameof(Negatives))]
    public void Benign_lines_raise_nothing(string fileName, string line)
    {
        Assert.Empty(RulesFiring(line, fileName));
    }

    [Fact]
    public void Every_rule_has_a_positive_test_a_known_category_and_a_remediation()
    {
        var tested = Positives.Select(row => (string)row[0]).ToHashSet();

        foreach (var rule in Rules.All)
        {
            Assert.True(tested.Contains(rule.Id), $"rule {rule.Id} has no positive test case");
            Assert.Contains(rule.Category, Rules.Categories);
            Assert.NotNull(Rules.OwaspFor(rule.Category));
            Assert.False(string.IsNullOrWhiteSpace(rule.Remediation));
            Assert.StartsWith("CWE-", rule.Cwe);
        }
        Assert.Equal(Rules.All.Count, Rules.All.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Findings_carry_the_normalized_schema()
    {
        using var sandbox = new ScanSandbox().File("src/app.py", "import os\nos.system('ping ' + host)\n");

        var finding = Assert.Single(sandbox.Scan().Findings);

        Assert.Equal("injection.os-command:src/app.py:2", finding.Id);
        Assert.Equal("src/app.py", finding.File);
        Assert.Equal(2, finding.Line);
        Assert.Equal("injection", finding.Category);
        Assert.Equal("CWE-78", finding.Cwe);
        Assert.Equal("A03", finding.Owasp);
        Assert.False(string.IsNullOrWhiteSpace(finding.Remediation));
        Assert.Null(finding.Context);
        Assert.False(finding.Suppressed);
    }
}

public class ScannerBehaviourTests
{
    private const string Weak = "digest = hashlib.md5(data)\n";

    [Theory]
    [InlineData("src/app.py", null)]
    [InlineData("tests/test_app.py", "test")]
    [InlineData("src/app_test.go", "test")]
    [InlineData("src/AppTests.cs", "test")]
    [InlineData("deps/rabbit/test/unit_SUITE.erl", "test")]
    [InlineData("web/app.spec.ts", "test")]
    [InlineData("README.md", "docs")]
    [InlineData("docs/guide/setup.py", "docs")]
    [InlineData("examples/demo.py", "docs")]
    [InlineData("src/latest.py", null)]          // "test" inside a word is not a test marker
    [InlineData("src/contest/app.py", null)]
    public void Test_and_documentation_files_are_marked_and_downgraded(string path, string? expected)
    {
        using var sandbox = new ScanSandbox().File(path, Weak);

        var finding = Assert.Single(sandbox.Scan().Findings);

        Assert.Equal(expected, finding.Context);
        Assert.Equal(expected is null ? "medium" : "low", finding.Confidence);
    }

    [Fact]
    public void Inline_marker_suppresses_but_keeps_the_finding_visible()
    {
        using var sandbox = new ScanSandbox().File("app.py",
            "a = hashlib.md5(x)  # cerberus:ignore\n" +
            "b = hashlib.md5(y)\n");

        var result = sandbox.Scan();

        Assert.Equal(2, result.Findings.Count);
        Assert.Equal(1, result.SuppressedCount);
        Assert.Equal(1, result.ActiveCount);
        Assert.False(result.Findings[0].Suppressed);   // active findings come first
        Assert.Equal(2, result.Findings[0].Line);
        Assert.True(result.Findings[1].Suppressed);
    }

    [Fact]
    public void Marker_on_a_comment_line_applies_to_the_next_line_only()
    {
        using var sandbox = new ScanSandbox().File("app.py",
            "# cerberus:ignore\n" +
            "a = hashlib.md5(x)\n" +
            "b = hashlib.md5(y)\n");

        var result = sandbox.Scan();

        Assert.Equal([true, false], result.Findings.OrderBy(f => f.Line).Select(f => f.Suppressed).ToArray());
    }

    [Fact]
    public void Marker_inside_code_on_the_previous_line_does_not_leak_downwards()
    {
        using var sandbox = new ScanSandbox().File("app.py",
            "note = 'cerberus:ignore'\n" +
            "a = hashlib.md5(x)\n");

        Assert.Equal(0, sandbox.Scan().SuppressedCount);
    }

    [Theory]
    [InlineData("cerberus:ignore crypto.weak-hash", true)]
    [InlineData("cerberus:ignore crypto", true)]                       // category prefix
    [InlineData("cerberus:ignore secret.jwt, crypto.weak-hash", true)]
    [InlineData("cerberus:ignore secret", false)]                      // a different rule
    [InlineData("cerberus:ignore crypto.weak", false)]                 // not a whole id segment
    public void Marker_can_name_the_rules_it_covers(string marker, bool suppressed)
    {
        using var sandbox = new ScanSandbox().File("app.py", $"a = hashlib.md5(x)  # {marker}\n");

        Assert.Equal(suppressed ? 1 : 0, sandbox.Scan().SuppressedCount);
    }

    [Fact]
    public void Ignore_file_excludes_paths_and_reports_how_many()
    {
        using var sandbox = new ScanSandbox()
            .File(".cerberusignore", "# vendored\nvendor-docs/\n*.generated.py\nsrc/legacy/old.py\n../outside\n")
            .File("vendor-docs/a/b.py", Weak)
            .File("src/x.generated.py", Weak)
            .File("src/legacy/old.py", Weak)
            .File("src/legacy/new.py", Weak)
            .File("src/app.py", Weak);

        var result = sandbox.Scan();

        Assert.Equal(["src/app.py", "src/legacy/new.py"], result.Findings.Select(f => f.File).Order().ToArray());
        Assert.Equal(3, result.FilesIgnored);   // a pattern without a slash matches at any depth
    }

    [Fact]
    public void Double_star_matches_at_any_depth()
    {
        using var sandbox = new ScanSandbox()
            .File(".cerberusignore", "**/*.generated.py\n")
            .File("x.generated.py", Weak)
            .File("a/b/c/y.generated.py", Weak)
            .File("a/app.py", Weak);

        var result = sandbox.Scan();

        Assert.Equal("a/app.py", Assert.Single(result.Findings).File);
        Assert.Equal(2, result.FilesIgnored);
    }

    [Fact]
    public void Ignore_file_in_the_scanned_directory_applies_relative_to_it()
    {
        using var sandbox = new ScanSandbox()
            .File("service/.cerberusignore", "generated/\n")
            .File("service/generated/a.py", Weak)
            .File("service/app.py", Weak)
            .File("generated/b.py", Weak);

        Assert.Equal("service/app.py", Assert.Single(sandbox.Scan("service").Findings).File);

        // Scanning from above only reads the root's ignore file, so nothing is excluded here.
        Assert.Equal(3, sandbox.Scan().Findings.Count);
    }

    [Fact]
    public void Binary_generated_and_oversized_files_are_skipped_and_counted()
    {
        var line = "x = hashlib.md5(y)\n";
        using var sandbox = new ScanSandbox()
            .Bytes("blob.dat", [.. "hashlib.md5(x)"u8.ToArray(), 0, 1, 2])
            .File("package-lock.json", line)
            .File("app.min.js", line)
            .File("logo.png", line)
            .File("huge.py", string.Concat(Enumerable.Repeat(line, 60_000)))
            .File("node_modules/lib/index.js", line)
            .File("app.py", line);

        var result = sandbox.Scan();

        Assert.Equal("app.py", Assert.Single(result.Findings).File);
        Assert.Equal(1, result.FilesScanned);
        Assert.Equal(5, result.FilesSkipped);   // node_modules is never entered, so it is not counted
    }

    [Fact]
    public void Very_long_lines_are_not_scanned()
    {
        using var sandbox = new ScanSandbox()
            .File("data.js", "var blob = '" + new string('a', 3000) + "'; eval(blob)\n" + "eval(x)\n");

        Assert.Equal(2, Assert.Single(sandbox.Scan().Findings).Line);
    }

    [Fact]
    public void Symbolic_links_are_not_followed_out_of_the_tree()
    {
        using var outside = new ScanSandbox().File("secret.py", Weak);
        using var sandbox = new ScanSandbox().File("app.py", "print('ok')\n");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(sandbox.Root, "linked-dir"), outside.Root);
            File.CreateSymbolicLink(Path.Combine(sandbox.Root, "linked.py"), Path.Combine(outside.Root, "secret.py"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;   // links need privileges on some platforms; nothing to test there
        }

        Assert.Empty(sandbox.Scan().Findings);
    }

    [Fact]
    public void Scanning_outside_the_root_is_refused()
    {
        using var sandbox = new ScanSandbox();

        Assert.Throws<UnauthorizedAccessException>(() => sandbox.Scan(".."));
        Assert.Throws<UnauthorizedAccessException>(() => sandbox.Scan("/etc"));
    }

    [Fact]
    public void Results_are_ordered_deterministically()
    {
        using var sandbox = new ScanSandbox();
        foreach (var name in new[] { "b.py", "a.py", "c/d.py", "c/a.py" }) sandbox.File(name, Weak + "os.system(x)\n");

        var first = sandbox.Scan().Findings.Select(f => f.Id).ToArray();
        var second = sandbox.Scan().Findings.Select(f => f.Id).ToArray();

        Assert.Equal(first, second);
        Assert.Equal(8, first.Length);
    }
}
