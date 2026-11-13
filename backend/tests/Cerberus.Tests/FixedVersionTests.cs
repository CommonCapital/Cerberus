using System.Net;
using System.Text;
using System.Text.Json;
using Cerberus.Scanning;

namespace Cerberus.Tests;

/// <summary>
/// Which version a dependency finding tells you to move to. An advisory lists
/// one fix per maintained branch; the advice has to be the one for the branch
/// in use, not whichever comes first.
/// </summary>
public class FixedVersionTests
{
    // --- ordering ----------------------------------------------------------------

    [Theory]
    [InlineData("1.22.11", "1.23.3", -1)]
    [InlineData("1.23.5", "1.23.3", 1)]
    [InlineData("2.9.10", "2.9.8", 1)]              // numbers, not text
    [InlineData("2.9.10.1", "2.9.10", 1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("v1.6.0", "1.6.0", 0)]
    [InlineData("1.24.0-rc.2", "1.24.0", -1)]       // a pre-release comes before its release
    [InlineData("1.24.0-rc.2", "1.24.0-rc.10", -1)]
    [InlineData("1.24.0-rc.1", "1.23.9", 1)]
    [InlineData("0.0.0-20200622213623-75b288015ac9", "0.0.0-20220314234659-1baeb1ce4c0b", -1)]   // Go pseudo-versions
    [InlineData("0.0.0-20220314234659-1baeb1ce4c0b", "0.17.0", -1)]
    [InlineData("2.4.2+incompatible", "2.4.2", 0)]  // build metadata does not order
    [InlineData("5.3.0.RELEASE", "5.3.0.RELEASE", 0)]
    public void Versions_are_ordered_by_their_numbers(string a, string b, int expected)
    {
        Assert.Equal(expected, Math.Sign(VersionOrder.Compare(a, b)));
        Assert.Equal(-expected, Math.Sign(VersionOrder.Compare(b, a)));
    }

    [Theory]
    [InlineData("1.23.3", "1.22.11,1.23.5,1.24.0-rc.2", "1.23.5")]    // the branch in use, not the first listed
    [InlineData("1.22.4", "1.22.11,1.23.5,1.24.0-rc.2", "1.22.11")]
    [InlineData("1.21.0", "1.22.11,1.23.5", "1.22.11")]               // a branch with no fix of its own: the next one up
    [InlineData("2.2.0", "1.11.27,2.2.9,3.0.1", "2.2.9")]
    [InlineData("3.0.0", "3.0.1,2.2.9,1.11.27", "3.0.1")]             // the advisory's order does not matter
    [InlineData("4.0.0", "1.11.27,2.2.9,3.0.1", null)]                // nothing above: no advice rather than a downgrade
    [InlineData("1.0.0", "", null)]
    public void The_fix_is_the_lowest_one_above_the_version_in_use(string current, string listed, string? expected)
    {
        var fixes = listed.Split(',', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(expected, VersionOrder.NextFix(current, fixes));
    }

    [Fact]
    public void No_list_means_no_fix()
    {
        Assert.Null(VersionOrder.NextFix("1.0.0", null));
    }

    // --- through the dependency check ------------------------------------------------

    /// <summary>Answers as OSV would: every queried package is affected by one advisory, whose text is given.</summary>
    private sealed class FakeOsv(string advisory) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body;
            if (request.Method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var count = doc.RootElement.GetProperty("queries").GetArrayLength();
                body = JsonSerializer.Serialize(new { results = Enumerable.Range(0, count).Select(_ => new { vulns = new[] { new { id = "GO-2025-0001" } } }) });
            }
            else
            {
                body = advisory;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private const string StdlibAdvisory = """
    {
      "id": "GO-2025-0001",
      "summary": "A flaw in a library maintained on three branches",
      "aliases": ["CVE-2025-0001"],
      "affected": [
        {
          "package": { "name": "example.com/lib", "ecosystem": "Go" },
          "ranges": [
            { "type": "GIT", "repo": "https://example.com/lib", "events": [ { "introduced": "0" }, { "fixed": "8a4c24f9bb85c2da5bf9eb1b2f3eac0e6b8a1f7d" } ] },
            { "type": "SEMVER", "events": [ { "introduced": "0" }, { "fixed": "1.22.11" }, { "introduced": "1.23.0-0" }, { "fixed": "1.23.5" }, { "introduced": "1.24.0-0" }, { "fixed": "1.24.0-rc.2" } ] }
          ]
        },
        {
          "package": { "name": "some/other/module", "ecosystem": "Go" },
          "ranges": [ { "type": "SEMVER", "events": [ { "introduced": "0" }, { "fixed": "1.23.4" } ] } ]
        }
      ]
    }
    """;

    private static async Task<Finding> CheckAsync(string goVersion, string advisory = StdlibAdvisory)
    {
        using var sandbox = new ScanSandbox().File("go.mod", $"module x\n\nrequire example.com/lib v{goVersion}\n");
        using var http = new HttpClient(new FakeOsv(advisory));
        var result = await new ScaService(new ScanOptions(sandbox.Root), new OsvClient(http)).ScanAsync(".", CancellationToken.None);
        Assert.True(result.Completed);
        return Assert.Single(result.Findings);
    }

    [Theory]
    [InlineData("1.23.3", "1.23.5")]
    [InlineData("1.22.4", "1.22.11")]
    public async Task A_finding_names_the_fix_on_the_branch_in_use(string inUse, string fix)
    {
        var finding = await CheckAsync(inUse);

        Assert.Equal(fix, finding.FixedVersion);
        Assert.EndsWith($"Fixed in {fix}.", finding.Evidence);
        Assert.Equal("CVE-2025-0001", finding.VulnId);
    }

    [Fact]
    public async Task A_commit_hash_is_never_offered_as_a_version()
    {
        // Only a range of commits says "fixed": there is no version to move to.
        var gitOnly = StdlibAdvisory.Replace("\"type\": \"SEMVER\"", "\"type\": \"GIT\"");
        var finding = await CheckAsync("1.23.3", gitOnly);

        Assert.Null(finding.FixedVersion);
        Assert.DoesNotContain("Fixed in", finding.Evidence);
    }

    [Fact]
    public async Task With_no_fix_above_the_version_in_use_none_is_suggested()
    {
        var finding = await CheckAsync("1.25.0");
        Assert.Null(finding.FixedVersion);
        Assert.DoesNotContain("Fixed in", finding.Evidence);
    }
}
