using System.Text.Json;
using System.Text.Json.Serialization;
using Cerberus.Api.Audit;
using Cerberus.Authorization;
using Cerberus.Data;
using Cerberus.Scanning;

namespace Cerberus.Api.Jobs;

public sealed record ScanRun(Scan Scan, ScanResult Static, ScaResult Sca, IReadOnlyList<Finding> Findings);

/// <summary>
/// Runs a Phase-1 scan (static pass + optional dependency check), stores it and
/// records it in the audit log. Shared by the synchronous <c>POST /scan</c> and
/// the background job worker so both behave identically. Authorization is the
/// caller's job and must already have happened.
/// </summary>
public sealed class ScanRunner(ScanService scanner, ScaService sca, CerberusDbContext db, AuditLog audit)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <exception cref="UnauthorizedAccessException">The path is outside the scan root.</exception>
    public async Task<ScanRun> RunAsync(
        string userId, string? engagementId, string targetPath, bool runSca, CancellationToken ct)
    {
        // Static (regex) pass is synchronous; SCA is an async OSV lookup.
        var result = scanner.Scan(targetPath, ct);
        var scaResult = runSca ? await sca.ScanAsync(targetPath, ct) : new ScaResult(0, 0, []);
        ct.ThrowIfCancellationRequested();   // a stopped run stores nothing

        // Merge both passes; re-sort by severity so the combined set is ranked.
        var findings = result.Findings.Concat(scaResult.Findings)
            .OrderByDescending(f => f.Severity)
            .ToList();

        var scan = new Scan
        {
            UserId = userId,
            TargetPath = targetPath,
            EngagementId = engagementId,
            FilesScanned = result.FilesScanned,
            FindingCount = findings.Count,
            ScaCompleted = scaResult.Completed,
            Findings = JsonSerializer.Serialize(findings, JsonOptions),
        };
        db.Scans.Add(scan);
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(userId, "scan.run", AuditOutcome.Ok,
            $"Scan {scan.Id}: {result.FilesScanned} files, {findings.Count} findings, dependency check {(scaResult.Completed ? "completed" : "not completed")}.",
            engagementId, targetPath, CapabilityTier.T0, CancellationToken.None);

        return new ScanRun(scan, result, scaResult, findings);
    }
}
