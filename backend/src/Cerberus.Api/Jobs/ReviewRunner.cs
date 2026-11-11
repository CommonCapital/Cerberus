using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerberus.Api.Audit;
using Cerberus.Authorization;
using Cerberus.Compliance;
using Cerberus.Core.Models;
using Cerberus.Data;
using Cerberus.Jobs;
using Cerberus.Scanning;
using Cerberus.Triage;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Jobs;

/// <param name="Catalogs">Catalog ids to assess against, in order.</param>
/// <param name="TriageModel">Model that reviews the findings; null for no model review.</param>
public sealed record ReviewJobParameters(
    List<string> Catalogs, bool Sca = true, string Exclusions = ScanExclusions.Ask,
    string? TriageModel = null, int MaxFindings = 15,
    string? AgentModel = null, int AgentSteps = Agents.SastAgent.DefaultSteps);

public sealed record PlanStep(string Id, string Title, string Detail);

/// <summary>
/// The plan of a review: what will happen, in order, stated before anything
/// runs (PLAN.md §6.5, plan → approve → execute). Deterministic — the same
/// request always yields the same plan — so what the operator approves is
/// exactly what is executed.
/// </summary>
public static class ReviewPlan
{
    public const string ScanStep = "scan";
    public const string TriageStep = "triage";
    public const string AgentStep = "sast";
    public const string ReportStep = "report";

    public static string AssessStep(string catalogId) => $"assess:{catalogId}";

    public static IReadOnlyList<PlanStep> Build(string targetPath, ReviewJobParameters parameters, CatalogLoader catalogs)
    {
        var steps = new List<PlanStep>
        {
            new(ScanStep, $"Scan \"{targetPath}\"",
                "Static rules for secrets, weak crypto, insecure configuration, injection sinks and unsafe deserialization" +
                (parameters.Sca ? ", plus a dependency check against OSV.dev (package names and versions are sent there)." : ". No dependency check.")),
        };

        if (parameters.TriageModel is { } model)
        {
            var provider = ModelRegistry.Find(model)?.Provider;
            var where = provider is { } p && !FindingTriager.IsLocal(p)
                ? $"Code around each finding is sent to {p}; credential-shaped values are masked first."
                : "The model runs locally; no code leaves this machine.";
            steps.Add(new(TriageStep, $"Review up to {parameters.MaxFindings} findings with {model}",
                $"The model judges each production-code finding as real, harmless or unsure. Advisory only. {where}"));
        }

        if (parameters.AgentModel is { } agentModel)
        {
            var provider = ModelRegistry.Find(agentModel)?.Provider;
            var where = provider is { } p && !FindingTriager.IsLocal(p)
                ? $"Whatever it reads is sent to {p}; credential-shaped values are masked first."
                : "The model runs locally; no code leaves this machine.";
            steps.Add(new(AgentStep, $"Have {agentModel} read the code",
                $"The SAST agent explores the code read-only, in up to {parameters.AgentSteps} steps, and reports vulnerabilities the rules cannot see. " +
                $"Its findings are a model's candidates and do not change compliance results. {where}"));
        }

        foreach (var id in parameters.Catalogs)
        {
            var catalog = catalogs.Get(id);
            steps.Add(new(AssessStep(id), $"Assess against {catalog?.Name ?? id}",
                $"{catalog?.ControlCount ?? 0} controls; those the scanner can judge get a verdict, the rest are left for you."));
        }

        steps.Add(new(ReportStep, "Write the report", "A Markdown summary of the findings and compliance results, stored with the review."));
        return steps;
    }

    /// <summary>The plan as the numbered list shown in the approval request.</summary>
    public static string Describe(IReadOnlyList<PlanStep> steps) =>
        string.Join("\n", steps.Select((s, i) => $"{i + 1}. {s.Title} — {s.Detail}"));
}

public sealed record ReviewRun(Review Review);

/// <summary>
/// The first orchestrator: carries out a review plan as one job — scan, optional
/// model triage, one compliance assessment per catalog, then a report — using
/// the same runners as the individual jobs, and reporting each plan step on
/// the run's stream. Authorization and approval happened before it starts.
/// </summary>
public sealed class ReviewRunner(
    ScanRunner scans, AssessmentRunner assessments, TriageRunner triage, Agents.SastAgentRunner sast,
    CatalogLoader catalogs, CerberusDbContext db, AuditLog audit)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<ReviewRun> RunAsync(Job job, ReviewJobParameters parameters, IRunReporter reporter, CancellationToken ct)
    {
        var plan = ReviewPlan.Build(job.TargetPath, parameters, catalogs);

        // The whole task list up front, so the operator sees what is still to come.
        await reporter.EventAsync(RunEventTypes.Plan, new { steps = plan.Select(s => new { id = s.Id, title = s.Title }) });
        foreach (var step in plan) await reporter.StepAsync(step.Id, step.Title, StepStatus.Pending);

        // The sub-runners report their own progress, findings and questions, but not their own steps.
        var inner = new PlanStepReporter(reporter);
        PlanStep Step(string id) => plan.First(s => s.Id == id);

        // 1. Scan
        await reporter.StepAsync(ReviewPlan.ScanStep, Step(ReviewPlan.ScanStep).Title, StepStatus.Running);
        var scan = await scans.RunAsync(job.UserId, job.EngagementId, job.TargetPath, parameters.Sca, ct, inner, parameters.Exclusions);
        await reporter.StepAsync(ReviewPlan.ScanStep, Step(ReviewPlan.ScanStep).Title, StepStatus.Completed);

        // 2. Model review of the findings (optional)
        IReadOnlyList<TriageResult> opinions = [];
        if (parameters.TriageModel is { } model)
        {
            await reporter.StepAsync(ReviewPlan.TriageStep, Step(ReviewPlan.TriageStep).Title, StepStatus.Running);
            await triage.RunAsync(job.UserId, new TriageJobParameters(scan.Scan.Id, model, parameters.MaxFindings), ct, inner);
            await db.Entry(scan.Scan).ReloadAsync(ct);
            opinions = TriageRunner.ReadTriage(scan.Scan.Triage);
            await reporter.StepAsync(ReviewPlan.TriageStep, Step(ReviewPlan.TriageStep).Title, StepStatus.Completed);
        }

        // 3. The SAST agent (optional): a model reads the code itself
        AgentRun? agentRun = null;
        if (parameters.AgentModel is { } agentModel)
        {
            await reporter.StepAsync(ReviewPlan.AgentStep, Step(ReviewPlan.AgentStep).Title, StepStatus.Running);
            agentRun = await sast.RunAsync(job, new Agents.SastJobParameters(agentModel, parameters.AgentSteps), inner, ct);
            await reporter.StepAsync(ReviewPlan.AgentStep, Step(ReviewPlan.AgentStep).Title, StepStatus.Completed);
        }

        // 4. One assessment per catalog, all from the same scan
        var results = new List<AssessmentRun>();
        foreach (var id in parameters.Catalogs)
        {
            var catalog = catalogs.Get(id) ?? throw new InvalidOperationException($"Catalog '{id}' is no longer installed.");
            var stepId = ReviewPlan.AssessStep(id);
            await reporter.StepAsync(stepId, Step(stepId).Title, StepStatus.Running);
            results.Add(await assessments.RunAsync(job.UserId, job.EngagementId, catalog, scan.Scan, job.TargetPath, parameters.Sca, ct, inner));
            await reporter.StepAsync(stepId, Step(stepId).Title, StepStatus.Completed);
        }

        // 5. Report
        await reporter.StepAsync(ReviewPlan.ReportStep, Step(ReviewPlan.ReportStep).Title, StepStatus.Running);
        ct.ThrowIfCancellationRequested();
        var review = new Review
        {
            UserId = job.UserId,
            EngagementId = job.EngagementId,
            JobId = job.Id,
            TargetPath = job.TargetPath,
            ScanId = scan.Scan.Id,
            AssessmentIds = JsonSerializer.Serialize(results.Select(r => r.Assessment.Id)),
            AgentRunId = agentRun?.Id,
            FindingCount = scan.Scan.FindingCount,
            Report = ReviewReport.Write(job.TargetPath, scan, results, opinions, DateTime.UtcNow, agentRun),
        };
        db.Reviews.Add(review);
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(job.UserId, "review.complete", AuditOutcome.Ok,
            $"Review {review.Id}: scan {scan.Scan.Id} ({scan.Scan.FindingCount} findings), {results.Count} assessment(s), {opinions.Count} model opinion(s){(agentRun is null ? "" : $", agent run {agentRun.Id} ({agentRun.FindingCount} reported)")}.",
            job.EngagementId, job.TargetPath, CapabilityTier.T0, CancellationToken.None);
        await reporter.StepAsync(ReviewPlan.ReportStep, Step(ReviewPlan.ReportStep).Title, StepStatus.Completed);

        return new ReviewRun(review);
    }

    /// <summary>Passes everything through except step events, which belong to the plan's own steps.</summary>
    private sealed class PlanStepReporter(IRunReporter inner) : IRunReporter
    {
        public Task StepAsync(string id, string title, string status) => Task.CompletedTask;

        public Task ProgressAsync(int done, int total, string? message = null) => inner.ProgressAsync(done, total, message);

        public Task FindingAsync(object finding) => inner.FindingAsync(finding);

        public Task TextAsync(string channel, string delta) => inner.TextAsync(channel, delta);

        public Task FlushTextAsync() => inner.FlushTextAsync();

        public Task EventAsync(string type, object data) => inner.EventAsync(type, data);

        public Task<OperatorAnswer?> AskAsync(OperatorQuestionSpec question, CancellationToken ct) => inner.AskAsync(question, ct);
    }
}

/// <summary>Writes a review's report. Plain Markdown, built only from what the review established.</summary>
public static class ReviewReport
{
    private const int MaxFindingsListed = 40;

    public static string Write(
        string targetPath, ScanRun scan, IReadOnlyList<AssessmentRun> assessments,
        IReadOnlyList<TriageResult> opinions, DateTime now, AgentRun? agentRun = null)
    {
        var active = scan.Findings.Where(f => !f.Suppressed).ToList();
        var production = active.Where(f => f.Context is null).ToList();
        var text = new StringBuilder();

        text.AppendLine($"# Security review of `{targetPath}`");
        text.AppendLine();
        text.AppendLine($"Generated {now:yyyy-MM-dd HH:mm} UTC by Cerberus. Scan `{scan.Scan.Id}`.");
        text.AppendLine();
        text.AppendLine("> Findings are candidates from pattern-based rules and an advisory database, not confirmed vulnerabilities. " +
                        "\"Compliant\" means only that the scanner found nothing in the categories mapped to a control.");
        text.AppendLine();

        text.AppendLine("## Summary");
        text.AppendLine();
        text.AppendLine($"- **Files scanned:** {scan.Static.FilesScanned} ({scan.Static.FilesSkipped} skipped as binary or generated, {scan.Static.FilesIgnored} excluded by the target's ignore file)");
        text.AppendLine($"- **Findings:** {active.Count} ({production.Count} in production code, {active.Count - production.Count} in tests or documentation)" +
                        (scan.Static.SuppressedCount > 0 ? $"; {scan.Static.SuppressedCount} more were suppressed by markers in the target" : ""));
        text.AppendLine($"- **By severity:** {BySeverity(active)}");
        text.AppendLine(scan.Sca.DependenciesScanned > 0 || scan.Sca.Completed
            ? $"- **Dependencies:** {scan.Sca.DependenciesScanned} checked, {scan.Sca.Findings.Count} known vulnerabilities" + (scan.Sca.Completed ? "" : " — **the check did not complete; this is partial**")
            : "- **Dependencies:** not checked");
        if (scan.Static.Truncated) text.AppendLine("- **Truncated:** the scan stopped at its finding limit; there may be more.");
        text.AppendLine();

        if (assessments.Count > 0)
        {
            text.AppendLine("## Compliance");
            text.AppendLine();
            text.AppendLine("| Catalog | Auto-assessed | Compliant | Non-compliant | Left for manual review | Score |");
            text.AppendLine("|---|---|---|---|---|---|");
            foreach (var a in assessments)
            {
                var r = a.Report;
                text.AppendLine($"| {r.CatalogName} | {r.Assessed} of {r.TotalControls} | {r.Compliant} | {r.NonCompliant} | {r.NotAssessed} | {(r.Score is { } s ? $"{s:0.#}%" : "n/a")} |");
            }
            text.AppendLine();
            text.AppendLine("The score covers automatically assessed controls only.");
            text.AppendLine();

            foreach (var a in assessments)
            {
                var failing = a.Report.Controls.Where(c => c.Verdict == ControlVerdict.NonCompliant).ToList();
                if (failing.Count == 0) continue;
                text.AppendLine($"### {a.Report.CatalogName}: non-compliant controls");
                text.AppendLine();
                foreach (var control in failing)
                {
                    text.AppendLine($"- **{control.ControlId} {control.Title}** — {control.Evidence.Count} finding(s)");
                }
                text.AppendLine();
            }
        }

        text.AppendLine("## Findings in production code");
        text.AppendLine();
        if (production.Count == 0)
        {
            text.AppendLine("None.");
        }
        else
        {
            var verdicts = opinions.ToDictionary(o => o.FindingId, o => o.Verdict);
            text.AppendLine("| Severity | Finding | Location | Model review |");
            text.AppendLine("|---|---|---|---|");
            foreach (var f in production.OrderByDescending(f => f.Severity).Take(MaxFindingsListed))
            {
                var verdict = verdicts.TryGetValue(f.Id, out var v)
                    ? v switch { TriageVerdict.TruePositive => "real", TriageVerdict.FalsePositive => "harmless", _ => "unsure" }
                    : "—";
                text.AppendLine($"| {f.Severity} | {Cell(f.Title)} | `{Cell(f.File)}:{f.Line}` | {verdict} |");
            }
            if (production.Count > MaxFindingsListed)
            {
                text.AppendLine();
                text.AppendLine($"…and {production.Count - MaxFindingsListed} more; the full list is in the scan.");
            }
        }
        text.AppendLine();

        if (opinions.Count > 0)
        {
            var model = opinions[0].Model;
            text.AppendLine($"Model review by `{model}` is advisory: it never removes a finding or changes a compliance verdict.");
            text.AppendLine();
        }

        if (agentRun is not null)
        {
            var reported = Agents.SastAgentRunner.ReadFindings(agentRun.Findings);
            text.AppendLine($"## Reported by the model (`{agentRun.Model}`)");
            text.AppendLine();
            text.AppendLine("> A model read the code and reported these. They are candidates for a person to check, are not part of the scan, " +
                            "and do not change any compliance result above. The code under review can try to mislead a model." +
                            (agentRun.Complete ? "" : " **This part of the review did not complete.**"));
            text.AppendLine();
            if (reported.Count == 0)
            {
                text.AppendLine("Nothing reported.");
            }
            else
            {
                text.AppendLine("| Severity | Finding | Location | Why |");
                text.AppendLine("|---|---|---|---|");
                foreach (var f in reported.OrderByDescending(f => f.Severity).Take(MaxFindingsListed))
                {
                    text.AppendLine($"| {f.Severity} | {Cell(f.Title)} | `{Cell(f.File)}:{f.Line}` | {Cell(Shorten(f.Description, 240))} |");
                }
            }
            text.AppendLine();
            if (!string.IsNullOrWhiteSpace(agentRun.Summary))
            {
                text.AppendLine("The model's own summary:");
                text.AppendLine();
                foreach (var line in Shorten(agentRun.Summary, 2000).Split('\n')) text.AppendLine($"> {Cell(line)}");
                text.AppendLine();
            }
        }

        text.AppendLine("## What to do next");
        text.AppendLine();
        text.AppendLine("1. Review the findings above, most severe first; fix or record why each is acceptable.");
        if (assessments.Any(a => a.Report.NotAssessed > 0))
        {
            text.AppendLine("2. Answer the controls left for manual review (export the assessment as a spreadsheet, complete it, import it back).");
        }
        text.AppendLine($"{(assessments.Any(a => a.Report.NotAssessed > 0) ? 3 : 2)}. Re-run the review to confirm.");
        return text.ToString();
    }

    private static string BySeverity(IReadOnlyList<Finding> findings) =>
        findings.Count == 0
            ? "none"
            : string.Join(", ", findings.GroupBy(f => f.Severity).OrderByDescending(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));

    private static string Shorten(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    /// <summary>Text from the target goes into a table cell: no pipes, no line breaks, no markup taking over the report.</summary>
    private static string Cell(string value) =>
        value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Replace("`", "'").Replace("<", "&lt;");
}
