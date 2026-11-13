using System.Text;
using System.Text.Json;
using Cerberus.Scanning;
using Cerberus.Triage;

namespace Cerberus.Tests;

/// <summary>
/// What a build output gives away: source maps, debug companions, build-machine
/// paths and credentials in minified text. Sample tokens are assembled at
/// runtime so nothing credential-shaped sits in the repository.
/// </summary>
public class ReleaseExposureTests
{
    private static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    private static string GithubToken => "ghp_" + Repeat("a1B2", 9);
    private static string AwsKey => "AKIA" + Repeat("A", 12) + "ABCD";

    private static string Map(string[] sources, string?[]? content = null, string? sourceRoot = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = 3,
            ["file"] = "app.min.js",
            ["sourceRoot"] = sourceRoot,
            ["sources"] = sources,
            ["sourcesContent"] = content,
            ["mappings"] = "AAAA",
        }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value));

    private static string[] Ids(ScanResult result) => result.Findings.Select(f => f.Id.Split(':')[0]).Order().ToArray();

    // --- source maps ---------------------------------------------------------------

    [Fact]
    public void A_source_map_with_the_sources_inside_is_a_medium_finding()
    {
        using var sandbox = new ScanSandbox().File("dist/app.js.map",
            Map(["webpack://app/src/auth.ts", "webpack://app/src/db.ts"], ["export const login = () => {}", null]));
        var result = sandbox.Scan("dist");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("release.source-map-sources:dist/app.js.map:1", finding.Id);
        Assert.Equal("release", finding.Category);
        Assert.Equal(Severity.Medium, finding.Severity);
        Assert.Equal("CWE-540", finding.Cwe);
        Assert.Equal("A05", finding.Owasp);
        Assert.Contains("1 of 2 source file(s)", finding.Evidence);
        Assert.Contains("webpack://app/src/auth.ts", finding.Evidence);
        Assert.Equal(1, result.ReleaseFilesInspected);
        Assert.Equal(1, result.FilesSkipped);   // still not read by the line rules
    }

    [Fact]
    public void A_source_map_without_sources_is_a_low_finding()
    {
        using var sandbox = new ScanSandbox().File("app.css.map", Map(["src/site.scss"]));
        var finding = Assert.Single(sandbox.Scan().Findings);
        Assert.Equal("release.source-map", finding.Id.Split(':')[0]);
        Assert.Equal(Severity.Low, finding.Severity);
        Assert.Contains("src/site.scss", finding.Evidence);
    }

    [Fact]
    public void An_index_map_is_read_through_its_sections()
    {
        var json = """{"version":3,"sections":[{"offset":{"line":0,"column":0},"map":{"version":3,"sources":["a.ts"],"sourcesContent":["let a = 1"],"mappings":"AAAA"}}]}""";
        using var sandbox = new ScanSandbox().File("bundle.js.map", json);
        Assert.Equal(["release.source-map-sources"], Ids(sandbox.Scan()));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"name":"world","regions":[]}""")]
    [InlineData("[1,2,3]")]
    public void A_map_file_that_is_not_a_source_map_is_left_alone(string content)
    {
        using var sandbox = new ScanSandbox().File("world.map", content);
        var result = sandbox.Scan();
        Assert.Empty(result.Findings);
        Assert.Equal(1, result.FilesSkipped);
    }

    [Fact]
    public void A_source_map_embedded_in_a_script_is_found()
    {
        var map = Convert.ToBase64String(Encoding.UTF8.GetBytes(Map(["src/pay.ts"], ["const fee = 3"])));
        using var sandbox = new ScanSandbox()
            .File("public/app.js", "console.log(1)\n//# sourceMappingURL=data:application/json;charset=utf-8;base64," + map + "\n")
            .File("public/plain.js", "console.log(2)\n//# sourceMappingURL=plain.js.map\n");
        var result = sandbox.Scan();

        var finding = Assert.Single(result.Findings);
        Assert.Equal("release.source-map-sources:public/app.js:1", finding.Id);
        Assert.StartsWith("An embedded source map", finding.Evidence);
        Assert.Equal(2, result.FilesScanned);   // an ordinary script is still scanned by the line rules
    }

    [Fact]
    public void A_broken_embedded_map_is_ignored()
    {
        using var sandbox = new ScanSandbox()
            .File("a.js", "x()\n//# sourceMappingURL=data:application/json;base64,!!!notbase64\n")
            .File("b.js", "x()\n//# sourceMappingURL=data:application/json;base64," + Convert.ToBase64String("{oops"u8.ToArray()) + "\n");
        Assert.Empty(sandbox.Scan().Findings);
    }

    // --- debug companions -------------------------------------------------------------

    [Fact]
    public void A_program_database_is_reported_whichever_format_it_is()
    {
        using var sandbox = new ScanSandbox()
            .Bytes("out/native.pdb", [.. "Microsoft C/C++ MSF 7.00\r\n"u8.ToArray(), 0x1A, (byte)'D', (byte)'S', 0, 0, 0])
            .Bytes("out/Service.pdb", [.. "BSJB"u8.ToArray(), 1, 0, 1, 0])
            .File("out/notes.pdb", "a protein data bank file, not symbols\n");
        var result = sandbox.Scan();

        Assert.Equal(["release.debug-symbols", "release.debug-symbols"], Ids(result));
        Assert.Equal(["out/Service.pdb", "out/native.pdb"], result.Findings.Select(f => f.File).Order(StringComparer.Ordinal).ToArray());
        Assert.All(result.Findings, f => Assert.Equal(Severity.Low, f.Severity));
        Assert.Equal(2, result.ReleaseFilesInspected);
    }

    [Fact]
    public void A_dsym_bundle_is_reported_once()
    {
        using var sandbox = new ScanSandbox()
            .File("App.app.dSYM/Contents/Info.plist", "<plist/>\n")
            .Bytes("App.app.dSYM/Contents/Resources/DWARF/App", [0xCF, 0xFA, 0xED, 0xFE, 0, 0, 0, 0]);
        var result = sandbox.Scan();

        var finding = Assert.Single(result.Findings);
        Assert.Equal("release.debug-symbols:App.app.dSYM:1", finding.Id);
        Assert.Equal(1, result.ReleaseFilesInspected);
        Assert.Equal(2, result.FilesSkipped);
    }

    // --- build-machine paths ------------------------------------------------------------

    [Theory]
    [InlineData("/home/alice/work/app/src/main.c", "/home/alice/work/app/src/main.c")]
    [InlineData("/Users/bob/dev/app/main.swift", "/Users/bob/dev/app/main.swift")]
    [InlineData(@"C:\Users\carol\src\app\obj\Release\app.pdb", @"C:\Users\carol\src\app\obj\Release\app.pdb")]
    [InlineData("file:///home/dave/app/a.ts", "/home/dave/app/a.ts")]
    public void A_home_directory_path_is_found(string embedded, string shown)
    {
        var issue = ReleaseExposure.BuildPaths(Encoding.ASCII.GetBytes("\0junk\0" + embedded + "\0more"));
        Assert.NotNull(issue);
        Assert.Equal("release.build-path", issue.RuleId);
        Assert.Contains(shown, issue.Evidence);
        Assert.Contains("1 home directory", issue.Evidence);
    }

    [Theory]
    [InlineData("https://example.com/app/home/settings/profile")]   // a route, not a path that starts here
    [InlineData("see /home/user/.config/tool for settings")]        // a placeholder name
    [InlineData(@"C:\Users\Public\Documents\readme.txt")]           // a shared location
    [InlineData("/home/")]
    [InlineData("/Users/x")]
    [InlineData("")]
    public void What_is_not_a_build_machine_path_is_not_reported(string text)
    {
        Assert.Null(ReleaseExposure.BuildPaths(Encoding.ASCII.GetBytes(text)));
    }

    [Fact]
    public void Several_accounts_are_counted()
    {
        var issue = ReleaseExposure.BuildPaths("/home/alice/a.c\0/home/alice/b.c\0/Users/bob/c.c\0"u8);
        Assert.Contains("2 home directories", issue!.Evidence);
    }

    [Fact]
    public void A_binary_with_a_build_path_inside_reports_it_beside_its_hardening()
    {
        // A minimal hardened PE (see BinaryHardeningTests) with a debug path appended.
        var pe = new byte[0x40 + 24 + 240];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z'; pe[0x3C] = 0x40;
        pe[0x40] = (byte)'P'; pe[0x41] = (byte)'E';
        pe[0x40 + 20] = 240; pe[0x40 + 22] = 0x22;
        var opt = 0x40 + 24;
        pe[opt] = 0x0b; pe[opt + 1] = 0x02;
        pe[opt + 70] = 0x60; pe[opt + 71] = 0x41;   // HIGH_ENTROPY_VA | DYNAMIC_BASE | NX_COMPAT | GUARD_CF
        pe[opt + 108] = 16;
        // A debug record: signature, GUID and age (binary), then the path of the .pdb on the build machine.
        byte[] file = [.. pe, .. "RSDS"u8, .. new byte[20], .. Encoding.ASCII.GetBytes(@"C:\Users\carol\src\app\obj\app.pdb"), 0];

        using var sandbox = new ScanSandbox().Bytes("app.exe", file);
        var result = sandbox.Scan();

        var finding = Assert.Single(result.Findings);
        Assert.Equal("release.build-path", finding.Id.Split(':')[0]);
        Assert.Equal("CWE-200", finding.Cwe);
        Assert.Equal("medium", finding.Confidence);
        Assert.Equal(1, result.BinariesInspected);
    }

    [Fact]
    public void A_source_map_built_in_a_home_directory_says_so()
    {
        using var sandbox = new ScanSandbox().File("app.js.map", Map(["/home/erin/proj/src/a.ts", "/home/erin/proj/src/b.ts"]));
        Assert.Equal(["release.build-path", "release.source-map"], Ids(sandbox.Scan()));

        // Inside JSON a Windows path has its backslashes doubled.
        using var windows = new ScanSandbox().File("app.js.map", Map([@"C:\Users\frank\proj\a.ts"]));
        Assert.Contains("release.build-path", Ids(windows.Scan()));
    }

    // --- credentials in built text --------------------------------------------------------

    [Fact]
    public void A_token_deep_in_a_minified_line_is_found_and_masked()
    {
        var bundle = "var a=1;" + Repeat("function f(){return 42};", 2000) + "var t=\"" + GithubToken + "\";" + Repeat("g();", 500);
        using var sandbox = new ScanSandbox().File("dist/app.min.js", "/* build */\n" + bundle + "\n");
        var result = sandbox.Scan("dist");

        var finding = Assert.Single(result.Findings);
        Assert.Equal("release.credential.github-token:dist/app.min.js:2", finding.Id);
        Assert.Equal("release", finding.Category);
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal("CWE-798", finding.Cwe);
        Assert.Equal(2, finding.Line);
        Assert.Contains("1 occurrence", finding.Evidence);
        Assert.Contains("[REDACTED len=", finding.Evidence);
        Assert.DoesNotContain(GithubToken, finding.Evidence);
    }

    [Fact]
    public void A_token_across_a_window_boundary_is_found_once()
    {
        // Place the token so that it straddles the end of the first 4096-character window.
        var bundle = new string('x', 4096 - 10) + " " + GithubToken + " " + new string('y', 6000);
        var issue = Assert.Single(ReleaseExposure.Credentials(bundle));
        Assert.Equal("release.credential.github-token", issue.RuleId);
        Assert.Contains("1 occurrence", issue.Evidence);
    }

    [Fact]
    public void Occurrences_are_counted_per_kind_and_line()
    {
        var text = "a='" + AwsKey + "';b='" + AwsKey + "';c='" + GithubToken + "'\nd='" + AwsKey + "'";
        var issues = ReleaseExposure.Credentials(text);

        Assert.Equal(3, issues.Count);
        Assert.Contains(issues, i => i.RuleId == "release.credential.aws-access-key" && i.Line == 1 && i.Evidence.StartsWith("2 occurrences"));
        Assert.Contains(issues, i => i.RuleId == "release.credential.github-token" && i.Line == 1);
        Assert.Contains(issues, i => i.RuleId == "release.credential.aws-access-key" && i.Line == 2 && i.Evidence.StartsWith("1 occurrence"));
        Assert.All(issues, i => Assert.DoesNotContain(AwsKey, i.Evidence));
    }

    [Fact]
    public void Loose_secret_rules_are_not_applied_to_minified_code()
    {
        // An assignment to something called "password" is noise in a megabyte of bundle.
        using var sandbox = new ScanSandbox().File("app.min.js", "var password='" + "correcthorsebattery" + "';x=hashlib.md5(y)\n");
        Assert.Empty(sandbox.Scan().Findings);
    }

    [Fact]
    public void A_credential_inside_the_sources_of_a_map_is_found_decoded()
    {
        // In the JSON the quote is written \u0027 and the line break \n: read raw, neither token would be found.
        using var sandbox = new ScanSandbox().File("app.js.map",
            Map(["src/cfg.ts", "src/b.ts"], ["export const key = '" + AwsKey + "'\n" + AwsKey, "const t =\n" + GithubToken]));
        var findings = sandbox.Scan().Findings;

        Assert.Equal(
            ["release.credential.aws-access-key", "release.credential.github-token", "release.source-map-sources"],
            findings.Select(f => f.Id.Split(':')[0]).Order().ToArray());
        var aws = findings.Single(f => f.Id.StartsWith("release.credential.aws-access-key:"));
        Assert.Equal(1, aws.Line);
        Assert.StartsWith("2 occurrences in the embedded sources", aws.Evidence);
        Assert.DoesNotContain(AwsKey, aws.Evidence);
    }

    [Fact]
    public void A_map_named_file_that_is_not_a_source_map_is_still_searched_for_tokens()
    {
        using var sandbox = new ScanSandbox().File("config.map", "{\"token\": \"" + GithubToken + "\"}\n");
        Assert.Equal("release.credential.github-token", Assert.Single(sandbox.Scan().Findings).Id.Split(':')[0]);
    }

    // --- the scan around it ------------------------------------------------------------------

    [Fact]
    public void Built_files_under_tests_are_low_confidence_and_ignored_ones_are_not_read()
    {
        using var sandbox = new ScanSandbox()
            .File(".cerberusignore", "vendor-dist/\n")
            .File("vendor-dist/lib.js.map", Map(["a.ts"], ["let a"]))
            .File("tests/fixtures/app.js.map", Map(["a.ts"], ["let a"]));
        var result = sandbox.Scan();

        var finding = Assert.Single(result.Findings);
        Assert.Equal("tests/fixtures/app.js.map", finding.File);
        Assert.Equal("test", finding.Context);
        Assert.Equal("low", finding.Confidence);
        Assert.Equal(1, result.FilesIgnored);
    }

    [Fact]
    public void Release_findings_are_not_sent_to_triage()
    {
        using var sandbox = new ScanSandbox()
            .File("app.js.map", Map(["a.ts"], ["let a"]))
            .File("app.py", "digest = hashlib.md5(data).hexdigest()\n");
        var findings = sandbox.Scan().Findings;
        Assert.Contains(findings, f => f.Category == "release");

        var selected = FindingTriager.Select(findings, 50, includeTestsAndDocs: true);
        Assert.NotEmpty(selected);
        Assert.DoesNotContain(selected, f => f.Category == "release");
    }
}
