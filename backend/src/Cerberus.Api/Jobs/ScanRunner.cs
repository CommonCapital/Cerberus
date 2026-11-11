using System.Text.Json;
using System.Text.Json.Serialization;
using Cerberus.Api.Audit;
using Cerberus.Authorization;
using Cerberus.Data;
using Cerberus.Jobs;
using Cerberus.Scanning;

namespace Cerberus.Api.Jobs;

/// <summary>What to do with the target's own .cerberusignore files and cerberus:ignore markers.</summary>
public static class ScanExclusions
{
    public const string Honour = "honour";
    public const string Ignore = "ignore";

    /// <summary>Ask the operator when the target actually excludes something. Jobs only.</summary>
    public const string Ask = "ask";

    public const string HonourLabel = "Honour them";
    public const string IgnoreLabel = "Scan everything";

    public static bool IsValid(string? value, bool allowAsk) =>
        value is Honour or Ignore || (allowAsk && value == Ask);
}

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
        string userId, string? engagementId, string targetPath, bool runSca, CancellationToken ct,
        IRunReporter? reporter = null, string exclusions = ScanExclusions.Honour)
    {
        reporter ??= NullRunReporter.Instance;
        bool exclusionsHonoured;

        // Static (regex) pass is synchronous; SCA is an async OSV lookup.
        await reporter.StepAsync("static", "Static analysis", StepStatus.Running);
        var honour = exclusions != ScanExclusions.Ignore;
        var result = scanner.Scan(targetPath, ct, honour);

        // The target itself decided what is hidden from the totals. Whether to go
        // along with that is the operator's call, not the target's and not ours.
        if (exclusions == ScanExclusions.Ask && (result.FilesIgnored > 0 || result.SuppressedCount > 0))
        {
            var answer = await reporter.AskAsync(new OperatorQuestionSpec(
                $"The target at \"{targetPath}\" excludes {result.FilesIgnored} file(s) through .cerberusignore and marks " +
                $"{result.SuppressedCount} finding(s) with cerberus:ignore. Honour the target's own exclusions?",
                [
                    new QuestionOption(ScanExclusions.HonourLabel, "Skip the ignored files and keep suppressed findings out of the totals. Right for code you maintain."),
                    new QuestionOption(ScanExclusions.IgnoreLabel, "Scan every file and count every finding. Right for code you do not trust."),
                ]), ct);

            // No answer (nobody to ask, or nobody answered in time): keep the default and say so.
            if (answer is null)
            {
                await reporter.EventAsync(RunEventTypes.Note, new { message = "No answer about the target's exclusions; they were honoured (the default)." });
            }
            else if (answer.Selected.Contains(ScanExclusions.IgnoreLabel))
            {
                honour = false;
                result = scanner.Scan(targetPath, ct, honourExclusions: false);
            }
        }
        exclusionsHonoured = honour;
        foreach (var finding in result.Findings.Where(f => !f.Suppressed)) await reporter.FindingAsync(finding);
        await reporter.ProgressAsync(result.FilesScanned, result.FilesScanned,
            $"{result.FilesScanned} files scanned, {result.ActiveCount} findings.");
        await reporter.StepAsync("static", "Static analysis", StepStatus.Completed);

        ScaResult scaResult;
        if (runSca)
        {
            await reporter.StepAsync("dependencies", "Dependency check", StepStatus.Running);
            scaResult = await sca.ScanAsync(targetPath, ct);
            foreach (var finding in scaResult.Findings) await reporter.FindingAsync(finding);
            await reporter.ProgressAsync(scaResult.DependenciesScanned, scaResult.DependenciesScanned,
                scaResult.Completed
                    ? $"{scaResult.DependenciesScanned} dependencies checked, {scaResult.Findings.Count} known vulnerabilities."
                    : "The dependency check did not complete; its results are partial.");
            await reporter.StepAsync("dependencies", "Dependency check", StepStatus.Completed);
        }
        else
        {
            scaResult = new ScaResult(0, 0, []);
            await reporter.StepAsync("dependencies", "Dependency check", StepStatus.Skipped);
        }
        ct.ThrowIfCancellationRequested();   // a stopped run stores nothing
        await reporter.StepAsync("store", "Store results", StepStatus.Running);

        // Merge both passes; re-sort by severity so the combined set is ranked.
        var findings = result.Findings.Concat(scaResult.Findings)
            .OrderBy(f => f.Suppressed)
            .ThenByDescending(f => f.Severity)
            .ToList();
        var activeCount = findings.Count(f => !f.Suppressed);

        var scan = new Scan
        {
            UserId = userId,
            TargetPath = targetPath,
            EngagementId = engagementId,
            FilesScanned = result.FilesScanned,
            FindingCount = activeCount,   // suppressed findings are stored but not counted
            ScaCompleted = scaResult.Completed,
            Findings = JsonSerializer.Serialize(findings, JsonOptions),
        };
        db.Scans.Add(scan);
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(userId, "scan.run", AuditOutcome.Ok,
            $"Scan {scan.Id}: {result.FilesScanned} files, {activeCount} findings ({result.SuppressedCount} suppressed, {result.FilesIgnored} files ignored{(result.Truncated ? ", truncated" : "")}{(exclusionsHonoured ? "" : "; the target's own exclusions were disregarded")}), dependency check {(scaResult.Completed ? "completed" : "not completed")}.",
            engagementId, targetPath, CapabilityTier.T0, CancellationToken.None);

        await reporter.StepAsync("store", "Store results", StepStatus.Completed);
        return new ScanRun(scan, result, scaResult, findings);
    }
}
