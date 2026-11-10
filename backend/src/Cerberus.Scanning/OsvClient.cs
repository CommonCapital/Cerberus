using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Cerberus.Scanning;

/// <summary>A vulnerability as resolved from OSV.dev, normalized for findings.</summary>
public sealed record OsvVuln(
    string Id,
    string? Cve,
    string Summary,
    Severity Severity,
    string? Cwe,
    string? FixedVersion,
    string Reference);

/// <summary>
/// Minimal read-only client for the public OSV.dev API
/// (https://osv.dev). SCA queries package coordinates against a free,
/// keyless vulnerability database — no target traffic, no credentials.
/// Uses <c>POST /v1/querybatch</c> to find affected packages, then
/// <c>GET /v1/vulns/{id}</c> for details on each unique advisory.
/// Degrades gracefully: on any transport/parse error it yields nothing
/// rather than failing the scan.
/// </summary>
/// <summary>Advisory ids per dependency, plus whether every lookup succeeded.</summary>
public sealed record OsvBatchResult(IReadOnlyList<IReadOnlyList<string>> Ids, bool Complete);

public sealed class OsvClient(HttpClient http)
{
    private const string BatchUrl = "https://api.osv.dev/v1/querybatch";
    private const string VulnUrl = "https://api.osv.dev/v1/vulns/";
    private const int BatchSize = 100;     // OSV accepts up to ~1000; stay conservative
    private const int MaxDetailFetches = 150;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// For each input dependency (same order), returns the list of advisory IDs
    /// affecting it (empty when clean). Network failures surface as all-empty
    /// lists with <see cref="OsvBatchResult.Complete"/> set to false.
    /// </summary>
    public async Task<OsvBatchResult> QueryBatchAsync(
        IReadOnlyList<Dependency> deps, CancellationToken ct)
    {
        var results = new List<IReadOnlyList<string>>(deps.Count);
        for (var i = 0; i < deps.Count; i++) results.Add([]);
        var complete = true;

        for (var start = 0; start < deps.Count; start += BatchSize)
        {
            var chunk = deps.Skip(start).Take(BatchSize).ToList();
            var ids = await QueryChunkAsync(chunk, ct);
            if (ids is null) { complete = false; continue; }
            for (var j = 0; j < ids.Count; j++) results[start + j] = ids[j];
        }
        return new OsvBatchResult(results, complete);
    }

    /// <summary>Returns null when the lookup failed (transport, HTTP status or parse error).</summary>
    private async Task<IReadOnlyList<IReadOnlyList<string>>?> QueryChunkAsync(
        IReadOnlyList<Dependency> chunk, CancellationToken ct)
    {
        var payload = new
        {
            queries = chunk.Select(d => new
            {
                version = d.Version,
                package = new { name = d.Name, ecosystem = d.Ecosystem },
            }).ToArray(),
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BatchUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var resultsEl) ||
                resultsEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var outList = new List<IReadOnlyList<string>>(chunk.Count);
            var k = 0;
            foreach (var r in resultsEl.EnumerateArray())
            {
                var ids = new List<string>();
                if (r.ValueKind == JsonValueKind.Object &&
                    r.TryGetProperty("vulns", out var vulns) && vulns.ValueKind == JsonValueKind.Array)
                {
                    foreach (var vuln in vulns.EnumerateArray())
                        if (vuln.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                            ids.Add(id.GetString()!);
                }
                outList.Add(ids);
                k++;
            }
            // Pad if the server returned fewer results than queries.
            while (outList.Count < chunk.Count) outList.Add([]);
            return outList;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Fetch and normalize advisory details for a set of unique IDs, keyed by id.</summary>
    public async Task<IReadOnlyDictionary<string, OsvVuln>> FetchDetailsAsync(
        IEnumerable<string> ids, string ecosystem, string packageName, CancellationToken ct)
    {
        var map = new Dictionary<string, OsvVuln>(StringComparer.Ordinal);
        var fetched = 0;
        foreach (var id in ids.Distinct())
        {
            if (fetched >= MaxDetailFetches) break;
            fetched++;
            var vuln = await FetchOneAsync(id, ecosystem, packageName, ct);
            if (vuln is not null) map[id] = vuln;
        }
        return map;
    }

    private async Task<OsvVuln?> FetchOneAsync(string id, string ecosystem, string packageName, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, VulnUrl + Uri.EscapeDataString(id));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var summary = Str(root, "summary") ?? Str(root, "details") ?? id;
            summary = Truncate(summary, 300);
            var cve = ExtractCve(root);
            var severity = ExtractSeverity(root);
            var cwe = ExtractCwe(root);
            var fixedVersion = ExtractFixedVersion(root, ecosystem, packageName);
            var reference = $"https://osv.dev/vulnerability/{id}";

            return new OsvVuln(id, cve, summary, severity, cwe, fixedVersion, reference);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? ExtractCve(JsonElement root)
    {
        if (root.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
            foreach (var a in aliases.EnumerateArray())
                if (a.ValueKind == JsonValueKind.String && a.GetString() is { } s && s.StartsWith("CVE-", StringComparison.OrdinalIgnoreCase))
                    return s;
        return null;
    }

    /// <summary>
    /// Prefer a CVSS v3/v4 base score (from the OSV severity vector) and bucket it;
    /// fall back to a textual GHSA severity; else Medium.
    /// </summary>
    private static Severity ExtractSeverity(JsonElement root)
    {
        double? score = null;
        if (root.TryGetProperty("severity", out var sev) && sev.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sev.EnumerateArray())
            {
                var vector = Str(s, "score");
                var parsed = Cvss.BaseScore(vector);
                if (parsed is { } p) score = Math.Max(score ?? 0, p);
            }
        }
        if (score is { } sc) return BucketCvss(sc);

        // GHSA text severity under database_specific.
        if (root.TryGetProperty("database_specific", out var ds) && ds.ValueKind == JsonValueKind.Object)
        {
            var text = Str(ds, "severity");
            if (text is not null) return BucketText(text);
        }
        return Severity.Medium;
    }

    private static Severity BucketCvss(double s) => s switch
    {
        >= 9.0 => Severity.Critical,
        >= 7.0 => Severity.High,
        >= 4.0 => Severity.Medium,
        > 0.0 => Severity.Low,
        _ => Severity.Info,
    };

    private static Severity BucketText(string t) => t.Trim().ToUpperInvariant() switch
    {
        "CRITICAL" => Severity.Critical,
        "HIGH" => Severity.High,
        "MODERATE" or "MEDIUM" => Severity.Medium,
        "LOW" => Severity.Low,
        _ => Severity.Medium,
    };

    private static string? ExtractCwe(JsonElement root)
    {
        if (root.TryGetProperty("database_specific", out var ds) && ds.ValueKind == JsonValueKind.Object &&
            ds.TryGetProperty("cwe_ids", out var cwes) && cwes.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cwes.EnumerateArray())
                if (c.ValueKind == JsonValueKind.String) return c.GetString();
        }
        return null;
    }

    /// <summary>Find the first "fixed" event for the affected package that matches our ecosystem.</summary>
    private static string? ExtractFixedVersion(JsonElement root, string ecosystem, string packageName)
    {
        if (!root.TryGetProperty("affected", out var affected) || affected.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var a in affected.EnumerateArray())
        {
            if (!a.TryGetProperty("package", out var pkg) || pkg.ValueKind != JsonValueKind.Object) continue;
            var eco = Str(pkg, "ecosystem");
            var name = Str(pkg, "name");
            if (name is null || !name.Equals(packageName, StringComparison.OrdinalIgnoreCase)) continue;
            if (eco is not null && !eco.StartsWith(ecosystem, StringComparison.OrdinalIgnoreCase)) continue;

            if (!a.TryGetProperty("ranges", out var ranges) || ranges.ValueKind != JsonValueKind.Array) continue;
            foreach (var range in ranges.EnumerateArray())
            {
                if (!range.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) continue;
                foreach (var ev in events.EnumerateArray())
                {
                    if (ev.TryGetProperty("fixed", out var fx) && fx.ValueKind == JsonValueKind.String)
                        return fx.GetString();
                }
            }
        }
        return null;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

/// <summary>Tiny CVSS v3/v4 base-score parser (enough to bucket OSV severity vectors).</summary>
internal static class Cvss
{
    public static double? BaseScore(string? vector)
    {
        if (string.IsNullOrWhiteSpace(vector)) return null;
        if (vector.StartsWith("CVSS:3", StringComparison.OrdinalIgnoreCase)) return CvssV3(vector);
        // CVSS v4 scoring is non-trivial; skip precise math and let callers fall back.
        return null;
    }

    private static double? CvssV3(string vector)
    {
        var metrics = ParseMetrics(vector);
        if (!metrics.TryGetValue("AV", out var av) || !metrics.TryGetValue("AC", out var ac) ||
            !metrics.TryGetValue("PR", out var pr) || !metrics.TryGetValue("UI", out var ui) ||
            !metrics.TryGetValue("S", out var scope) || !metrics.TryGetValue("C", out var c) ||
            !metrics.TryGetValue("I", out var integ) || !metrics.TryGetValue("A", out var a))
        {
            return null;
        }

        var scopeChanged = scope == "C";
        var avV = av switch { "N" => 0.85, "A" => 0.62, "L" => 0.55, "P" => 0.2, _ => 0.0 };
        var acV = ac == "L" ? 0.77 : 0.44;
        var prV = pr switch
        {
            "N" => 0.85,
            "L" => scopeChanged ? 0.68 : 0.62,
            "H" => scopeChanged ? 0.5 : 0.27,
            _ => 0.0,
        };
        var uiV = ui == "N" ? 0.85 : 0.62;
        var cV = Impact(c); var iV = Impact(integ); var aV = Impact(a);

        var iscBase = 1 - (1 - cV) * (1 - iV) * (1 - aV);
        var impact = scopeChanged
            ? 7.52 * (iscBase - 0.029) - 3.25 * Math.Pow(iscBase - 0.02, 15)
            : 6.42 * iscBase;
        var exploitability = 8.22 * avV * acV * prV * uiV;

        if (impact <= 0) return 0.0;
        var raw = scopeChanged
            ? Math.Min(1.08 * (impact + exploitability), 10)
            : Math.Min(impact + exploitability, 10);
        return RoundUp(raw);
    }

    private static double Impact(string v) => v switch { "H" => 0.56, "L" => 0.22, _ => 0.0 };

    private static Dictionary<string, string> ParseMetrics(string vector)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in vector.Split('/'))
        {
            var kv = part.Split(':', 2);
            if (kv.Length == 2) map[kv[0]] = kv[1];
        }
        return map;
    }

    // CVSS "round up to one decimal" per spec.
    private static double RoundUp(double input) => Math.Ceiling(input * 10) / 10;
}
