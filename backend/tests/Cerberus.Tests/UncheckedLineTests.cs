using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cerberus.Api.Jobs;
using Cerberus.Scanning;

namespace Cerberus.Tests;

/// <summary>
/// A rule that runs out of time on a line. It is tried again with a longer
/// limit; a line that runs out of that too is reported as not checked, because
/// a finding may be missing there — it is never passed over in silence.
/// </summary>
public class UncheckedLineTests
{
    // Catastrophic backtracking: on "aaa…ab" this does not finish in any time a test can wait for.
    private static readonly Regex Hopeless = new(@"^(a+)+$", RegexOptions.None, TimeSpan.FromMilliseconds(50));
    private static readonly string Pathological = new string('a', 48) + "b";

    private static Rule RuleWith(Regex pattern, string id = "test.slow") =>
        new(id, "A rule under test", Severity.High, "injection", "CWE-1333", "high", pattern, "Fix it.");

    [Fact]
    public void A_match_that_runs_out_of_time_is_tried_again_with_a_longer_limit()
    {
        // A limit no real search fits into; the second try has the time it needs.
        var impatient = new Regex(@"needle\d+", RegexOptions.None, TimeSpan.FromTicks(1));
        var haystack = new string('x', 200_000) + "needle42";

        Assert.True(PatientMatch.IsMatch(impatient, haystack));
        Assert.Equal("needle42", Assert.Single(PatientMatch.Matches(impatient, haystack)!).Value);
        Assert.False(PatientMatch.IsMatch(impatient, new string('x', 200_000)));
    }

    [Fact]
    public void A_match_that_runs_out_of_time_twice_is_undecided_not_false()
    {
        Assert.Null(PatientMatch.IsMatch(Hopeless, Pathological));
        Assert.Null(PatientMatch.Matches(Hopeless, Pathological));
        Assert.True(PatientMatch.IsMatch(Hopeless, "aaaa"));   // the same pattern on an easy line decides at once
    }

    [Fact]
    public void A_line_a_rule_could_not_check_is_reported_with_its_place_and_rule()
    {
        using var sandbox = new ScanSandbox()
            .File("src/app.py", "fine = 1\n" + Pathological + "\nalso_fine = 2\n")
            .File("src/other.py", "aaaa\n");
        var scanner = new ScanService(new ScanOptions(sandbox.Root, [RuleWith(Hopeless)]));

        var result = scanner.Scan(".");

        var line = Assert.Single(result.UncheckedLines);
        Assert.Equal(new UncheckedLine("src/app.py", 2, "test.slow"), line);
        // The rule still did its work on the lines it could decide.
        Assert.Equal("test.slow:src/other.py:1", Assert.Single(result.Findings).Id);
        Assert.Equal(2, result.FilesScanned);
    }

    [Fact]
    public void A_scan_where_every_line_was_decided_says_nothing_was_left_unchecked()
    {
        using var sandbox = new ScanSandbox().File("app.py", "digest = hashlib.md5(data).hexdigest()\n");
        var result = sandbox.Scan();

        Assert.NotEmpty(result.Findings);
        Assert.Empty(result.UncheckedLines);
        Assert.Equal("", ScanRunner.UncheckedNote(result));
    }

    [Fact]
    public void The_note_says_how_many_lines_were_not_checked_and_why()
    {
        ScanResult With(int lines) => new(1, [], Unchecked: Enumerable.Range(1, lines).Select(n => new UncheckedLine("a.py", n, "r")).ToList());

        Assert.Equal(" 1 line was not fully checked: a rule ran out of time on it, also on a second try.", ScanRunner.UncheckedNote(With(1)));
        Assert.Equal(" 3 lines were not fully checked: a rule ran out of time on them, also on a second try.", ScanRunner.UncheckedNote(With(3)));
    }
}

/// <summary>What the API says about lines a rule could not check.</summary>
[Collection("api")]
public class UncheckedLineApiTests(ApiHost host)
{
    [Fact]
    public async Task A_scan_answers_and_stores_the_list_of_lines_left_unchecked()
    {
        var (client, _) = await host.NewUserAsync();
        var scan = await (await client.PostAsJsonAsync("/scan", new { path = "app", sca = false })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Array, scan.GetProperty("uncheckedLines").ValueKind);
        Assert.Equal(0, scan.GetProperty("uncheckedLines").GetArrayLength());

        var stored = await client.GetFromJsonAsync<JsonElement>($"/scans/{scan.GetProperty("id").GetString()}");
        Assert.Equal(JsonValueKind.Array, stored.GetProperty("uncheckedLines").ValueKind);
        Assert.Equal(0, stored.GetProperty("uncheckedLines").GetArrayLength());
    }
}
