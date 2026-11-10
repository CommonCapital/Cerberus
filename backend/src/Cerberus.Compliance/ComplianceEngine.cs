using Cerberus.Scanning;

namespace Cerberus.Compliance;

/// <summary>The automated verdict for a single control.</summary>
public enum ControlVerdict
{
    /// <summary>Control with a scanner mapping and no triggering findings.</summary>
    Compliant,

    /// <summary>Control with a scanner mapping and triggering findings present.</summary>
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
/// Deterministic Phase-2 compliance engine. Each catalog control may declare
/// <c>scannerCategories</c> (the mapping is catalog data, not code) — the
/// Phase-1 scanner finding categories that signal non-compliance. Verdicts:
/// a control with any mapped (triggering) finding is Non-compliant and the
/// findings are attached as evidence; a mapped control
/// with no triggering findings is Compliant; everything else is Not-assessed and
/// flagged for manual review. No LLM, no network — fully explainable and local.
/// </summary>
public sealed class ComplianceEngine
{
    private const string DependencyCategory = "dependency";

    
    /// <param name="dependencyCheckCompleted">
    /// Whether the dependency (SCA) check covered the target. When false, a
    /// control mapped to the <c>dependency</c> category cannot be Compliant.
    /// </param>
    public ComplianceReport Evaluate(
        Catalog catalog, IReadOnlyList<Finding> findings, bool dependencyCheckCompleted = true)
    {
        // Findings the target suppressed are reported but never count against a control.
        var byCategory = findings
            .Where(f => !f.Suppressed)
            .GroupBy(f => f.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Finding>)g.ToList(), StringComparer.OrdinalIgnoreCase);

        var results = new List<ControlResult>(catalog.ControlCount);

        foreach (var section in catalog.Sections)
        {
            foreach (var control in section.Controls)
            {
                results.Add(Assess(section.Name, control, byCategory, dependencyCheckCompleted));
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
        string section, Control control, IReadOnlyDictionary<string, IReadOnlyList<Finding>> byCategory,
        bool dependencyCheckCompleted)
    {
        var categories = control.ScannerCategories;
        if (categories is not { Count: > 0 })
        {
            var reason = control.AutoEvaluable
                ? "Marked auto-evaluable, but the current passive scanner emits no signal for this control — manual evidence required."
                : "Governance/process control — requires manual review and documented evidence.";
            return new ControlResult(control.Id, section, control.Title,
                ControlVerdict.NotAssessed, control.AutoEvaluable, reason, []);
        }

        var matched = categories
            .SelectMany(c => byCategory.TryGetValue(c, out var fs) ? fs : [])
            .OrderByDescending(f => f.Severity)
            .ToList();

        // A control is judged on production code. Hits in test code or
        // documentation are overwhelmingly fixtures and examples, so they are
        // disclosed in the rationale but do not fail the control on their own.
        var evidence = matched.Where(f => f.Context is null).ToList();
        var aside = matched.Count - evidence.Count;
        var asideNote = aside == 0 ? string.Empty
            : $" {aside} further finding(s) in test or documentation files were not counted; review them in the scan.";

        if (evidence.Count > 0)
        {
            var cats = string.Join(", ", categories);
            return new ControlResult(control.Id, section, control.Title,
                ControlVerdict.NonCompliant, true,
                $"{evidence.Count} scanner finding(s) in mapped categories ({cats}) indicate non-compliance.{asideNote}",
                evidence);
        }

        // Absence of findings only counts where the scanner actually looked.
        if (!dependencyCheckCompleted &&
            categories.Contains(DependencyCategory, StringComparer.OrdinalIgnoreCase))
        {
            return new ControlResult(control.Id, section, control.Title,
                ControlVerdict.NotAssessed, true,
                "No findings in production code, but the dependency (SCA) check did not run or did not complete, so this control cannot be judged. Re-run the scan with dependency analysis enabled and network access to OSV.dev." + asideNote,
                []);
        }

        return new ControlResult(control.Id, section, control.Title,
            ControlVerdict.Compliant, true,
            $"No scanner findings in production code in mapped categories ({string.Join(", ", categories)}).{asideNote}",
            []);
    }
}
