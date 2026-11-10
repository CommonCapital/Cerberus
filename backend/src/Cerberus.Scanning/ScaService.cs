namespace Cerberus.Scanning;

public sealed record ScaResult(
    int ManifestsScanned,
    int DependenciesScanned,
    IReadOnlyList<Finding> Findings,
    /// <summary>
    /// True when every discovered dependency was actually checked against OSV.
    /// False when the lookup was skipped, failed, or the dependency list was
    /// truncated — "no dependency findings" then proves nothing.
    /// </summary>
    bool Completed = false);

/// <summary>
/// Phase-1 software-composition analysis (SCA): discovers dependency
/// manifests under the (confined) target, resolves each dependency's known
/// vulnerabilities via OSV.dev, and emits normalized <see cref="Finding"/>s —
/// one per (dependency, advisory). Passive and read-only (tier T0): it queries
/// a public vulnerability database about package coordinates and never touches
/// the target over the network. Confinement mirrors <see cref="ScanService"/>.
/// </summary>
public sealed class ScaService(ScanOptions options, OsvClient osv)
{
    private const int MaxDependencies = 2000;

    public async Task<ScaResult> ScanAsync(string relativePath, CancellationToken ct)
    {
        var root = Path.GetFullPath(options.ScanRoot);
        var target = Path.GetFullPath(Path.Combine(root, relativePath));

        if (!target.Equals(root, StringComparison.Ordinal) &&
            !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Path is outside the scan root.");
        }

        var (deps, manifests) = Dependencies.Discover(target, root);
        var truncated = deps.Count > MaxDependencies;
        if (truncated) deps = deps.Take(MaxDependencies).ToList();
        if (deps.Count == 0) return new ScaResult(manifests.Count, 0, [], Completed: true);

        var batch = await osv.QueryBatchAsync(deps, ct);
        var vulnIdsPerDep = batch.Ids;

        // Detail cache so an advisory affecting many packages is fetched once
        // per (id, ecosystem, package) — fixed-version is package-specific.
        var cache = new Dictionary<string, OsvVuln?>(StringComparer.Ordinal);
        var findings = new List<Finding>();

        for (var i = 0; i < deps.Count; i++)
        {
            var dep = deps[i];
            var ids = vulnIdsPerDep[i];
            if (ids.Count == 0) continue;

            foreach (var id in ids)
            {
                var cacheKey = $"{id}|{dep.Ecosystem}|{dep.Name}";
                if (!cache.TryGetValue(cacheKey, out var vuln))
                {
                    var details = await osv.FetchDetailsAsync([id], dep.Ecosystem, dep.Name, ct);
                    vuln = details.TryGetValue(id, out var v) ? v : null;
                    cache[cacheKey] = vuln;
                }
                findings.Add(ToFinding(dep, id, vuln));
            }
        }

        findings.Sort((a, b) => b.Severity.CompareTo(a.Severity));
        return new ScaResult(manifests.Count, deps.Count, findings, Completed: batch.Complete && !truncated);
    }

    private static Finding ToFinding(Dependency dep, string id, OsvVuln? vuln)
    {
        var cve = vuln?.Cve;
        var label = cve ?? id;
        var severity = vuln?.Severity ?? Severity.Medium;
        var title = $"Vulnerable dependency: {dep.Name}@{dep.Version} ({label})";
        var summary = vuln?.Summary ?? $"{dep.Name}@{dep.Version} is affected by {id}.";
        var fix = vuln?.FixedVersion;
        var evidence = fix is not null
            ? $"{summary} Fixed in {fix}."
            : summary;

        return new Finding(
            Id: $"sca.{dep.Ecosystem}:{dep.Name}@{dep.Version}:{id}",
            Title: title,
            Severity: severity,
            Category: "dependency",
            Cwe: vuln?.Cwe ?? "CWE-1395",   // dependency on vulnerable third-party component
            File: dep.File,
            Line: dep.Line,
            Evidence: evidence,
            Confidence: "high",
            Package: dep.Name,
            Version: dep.Version,
            FixedVersion: fix,
            VulnId: cve ?? id,
            Reference: vuln?.Reference ?? $"https://osv.dev/vulnerability/{id}");
    }
}
