using Cerberus.Scanning;

namespace Cerberus.Compliance;

/// <summary>The automated verdict for a single control.</summary>
public enum ControlVerdict
{
    /// <summary>Auto-evaluable control with a scanner mapping and no triggering findings.</summary>
    Compliant,

    /// <summary>Auto-evaluable control with triggering findings present.</summary>
    NonCompliant,

    /// <summary>No automated signal available — requires manual review / evidence.</summary>
    NotAssessed,
}

public sealed record ControlResult(
    string ControlId,
    string Section,
    string Title,
    ControlVerdict Verdict,
    bool AutoEvaluable,
    string Rationale,
    IReadOnlyList<Finding> Evidence);

public sealed record ComplianceReport(
    string CatalogId,
    string CatalogName,
    string CatalogVersion,
    int TotalControls,
    int Assessed,
    int Compliant,
    int NonCompliant,
    int NotAssessed,
    /// <summary>Compliant / (Compliant + NonCompliant), as a 0–100 percentage of automatically assessed controls. Null when nothing could be assessed.</summary>
    double? Score,
    IReadOnlyList<ControlResult> Controls);

/// <summary>
/// Deterministic Phase-2 compliance engine. It maps a catalog's auto-evaluable
/// controls onto Phase-1 scanner finding categories and derives a verdict:
/// a control with any mapped (triggering) finding is Non-compliant and the
/// findings are attached as evidence; an auto-evaluable control with a mapping
/// and no triggering findings is Compliant; everything else is Not-assessed and
/// flagged for manual review. No LLM, no network — fully explainable and local.
/// </summary>
public sealed class ComplianceEngine
{
    /// <summary>
    /// Control → scanner finding categories that signal non-compliance.
    /// Catalog marks some controls <c>autoEvaluable</c>, but the MVP scanner only
    /// emits signals for a subset; controls without a mapping stay Not-assessed.
    /// Scanner categories today: secret, crypto, config, injection.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> Mapping = new Dictionary<string, string[]>
    {
        // Vulnerability & patch management — any code-level weakness counts.
        ["GSG-07"] = ["injection", "crypto", "config"],
        ["SAAS-14"] = ["injection", "crypto", "config"],

        // Secure log-on / identity / administration — hardcoded credentials.
        ["APP-01"] = ["secret"],
        ["APP-02"] = ["secret"],
        ["APP-05"] = ["secret"],
        ["SAAS-16"] = ["secret"],

        // Encryption controls — weak hashing / crypto misuse.
        ["APP-03"] = ["crypto"],
        ["SAAS-10"] = ["crypto"],

        // In-transit encryption & certificates — disabled TLS verification.
        ["APP-04"] = ["config"],
        ["SAAS-02"] = ["config"],
        ["SAAS-03"] = ["config"],

        // Development security — unsafe dynamic execution / injection sinks.
        ["APP-07"] = ["injection"],
    };

    public ComplianceReport Evaluate(Catalog catalog, IReadOnlyList<Finding> findings)
    {
        var byCategory = findings
            .GroupBy(f => f.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Finding>)g.ToList(), StringComparer.OrdinalIgnoreCase);

        var results = new List<ControlResult>(catalog.ControlCount);

        foreach (var section in catalog.Sections)
        {
            foreach (var control in section.Controls)
            {
                results.Add(Assess(section.Name, control, byCategory));
            }
        }

        var compliant = results.Count(r => r.Verdict == ControlVerdict.Compliant);
        var nonCompliant = results.Count(r => r.Verdict == ControlVerdict.NonCompliant);
        var notAssessed = results.Count(r => r.Verdict == ControlVerdict.NotAssessed);
        var assessed = compliant + nonCompliant;

        double? score = assessed == 0 ? null : Math.Round(100.0 * compliant / assessed, 1);

        return new ComplianceReport(
            catalog.Id, catalog.Name, catalog.Version,
            TotalControls: results.Count,
            Assessed: assessed,
            Compliant: compliant,
            NonCompliant: nonCompliant,
            NotAssessed: notAssessed,
            Score: score,
            Controls: results);
    }

    private static ControlResult Assess(
        string section, Control control, IReadOnlyDictionary<string, IReadOnlyList<Finding>> byCategory)
    {
        if (!control.AutoEvaluable || !Mapping.TryGetValue(control.Id, out var categories))
        {
            var reason = control.AutoEvaluable
                ? "Marked auto-evaluable, but the current passive scanner emits no signal for this control — manual evidence required."
                : "Governance/process control — requires manual review and documented evidence.";
            return new ControlResult(control.Id, section, control.Title,
                ControlVerdict.NotAssessed, control.AutoEvaluable, reason, []);
        }

        var evidence = categories
            .SelectMany(c => byCategory.TryGetValue(c, out var fs) ? fs : [])
            .OrderByDescending(f => f.Severity)
            .ToList();

        if (evidence.Count > 0)
        {
            var cats = string.Join(", ", categories);
            return new ControlResult(control.Id, section, control.Title,
                ControlVerdict.NonCompliant, true,
                $"{evidence.Count} scanner finding(s) in mapped categories ({cats}) indicate non-compliance.",
                evidence);
        }

        return new ControlResult(control.Id, section, control.Title,
            ControlVerdict.Compliant, true,
            $"No scanner findings in mapped categories ({string.Join(", ", categories)}).",
            []);
    }
}
