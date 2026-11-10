using System.Text.Json;
using System.Text.Json.Serialization;
using Cerberus.Api.Audit;
using Cerberus.Authorization;
using Cerberus.Compliance;
using Cerberus.Data;
using Cerberus.Scanning;

namespace Cerberus.Api.Jobs;

public sealed record AssessmentRun(ComplianceAssessment Assessment, ComplianceReport Report, bool DependencyCheckCompleted);

/// <summary>
/// Runs a Phase-2 compliance assessment — from a stored scan's findings or a
/// fresh scan of a path — stores it and records it in the audit log. Shared by
/// the synchronous <c>POST /compliance/assess</c> and the job worker.
/// Authorization is the caller's job and must already have happened.
/// </summary>
public sealed class AssessmentRunner(
    ScanService scanner, ScaService sca, ComplianceEngine engine, CerberusDbContext db, AuditLog audit)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <param name="scan">Reuse this scan's findings; null to scan <paramref name="targetPath"/> now.</param>
    /// <exception cref="UnauthorizedAccessException">The path is outside the scan root.</exception>
    public async Task<AssessmentRun> RunAsync(
        string userId, string? engagementId, Catalog catalog, Scan? scan, string targetPath, bool runSca,
        CancellationToken ct)
    {
        IReadOnlyList<Finding> findings;
        bool dependencyCheckCompleted;

        if (scan is not null)
        {
            findings = DeserializeFindings(scan.Findings);
            targetPath = scan.TargetPath;
            dependencyCheckCompleted = scan.ScaCompleted;
        }
        else
        {
            var staticFindings = scanner.Scan(targetPath, ct).Findings;
            var scaResult = runSca ? await sca.ScanAsync(targetPath, ct) : new ScaResult(0, 0, []);
            findings = staticFindings.Concat(scaResult.Findings).ToList();
            dependencyCheckCompleted = scaResult.Completed;
        }
        ct.ThrowIfCancellationRequested();   // a stopped run stores nothing

        var report = engine.Evaluate(catalog, findings, dependencyCheckCompleted);

        var assessment = new ComplianceAssessment
        {
            UserId = userId,
            CatalogId = report.CatalogId,
            TargetPath = targetPath,
            ScanId = scan?.Id,
            EngagementId = engagementId,
            TotalControls = report.TotalControls,
            Compliant = report.Compliant,
            NonCompliant = report.NonCompliant,
            NotAssessed = report.NotAssessed,
            Score = report.Score,
            Matrix = JsonSerializer.Serialize(report.Controls, JsonOptions),
        };
        db.ComplianceAssessments.Add(assessment);
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(userId, "compliance.assess", AuditOutcome.Ok,
            $"Assessment {assessment.Id} against {report.CatalogId}: {report.Compliant} compliant, {report.NonCompliant} non-compliant, {report.NotAssessed} not assessed.",
            engagementId, targetPath, CapabilityTier.T0, CancellationToken.None);

        return new AssessmentRun(assessment, report, dependencyCheckCompleted);
    }

    private static IReadOnlyList<Finding> DeserializeFindings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Finding>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
