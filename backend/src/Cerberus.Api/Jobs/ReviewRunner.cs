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
    string? AgentModel = null, int AgentSteps = Agents.SastAgent.DefaultSteps, List<string>? Agents = null)
{
    /// <summary>The agents this review dispatches: none without a model; the SAST agent alone unless others are named.</summary>
    public IReadOnlyList<string> AgentsToRun =>
        AgentModel is null ? [] : Agents is { Count: > 0 } ? Agents : [ReviewAgents.Sast];
}

/// <summary>The model-driven agents a review can dispatch.</summary>
public static class ReviewAgents
{
    public const string Sast = "sast";
    public const string ThreatModel = "threat-model";
    public const string Evidence = "evidence";

    public static readonly IReadOnlyList<string> All = [Sast, ThreatModel, Evidence];
}

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
    public const string ThreatModelStep = "threat-model";

    public static string EvidenceStep(string catalogId) => $"evidence:{catalogId}";
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

        var agents = parameters.AgentsToRun;
        var agentModel = parameters.AgentModel;
        var agentProvider = agentModel is null ? null : ModelRegistry.Find(agentModel)?.Provider;
        var sentTo = agentProvider is { } remote && !FindingTriager.IsLocal(remote)
            ? $"Whatever it reads is sent to {remote}; credential-shaped values are masked first."
            : "The model runs locally; no code leaves this machine.";

        if (agents.Contains(ReviewAgents.Sast))
        {
            steps.Add(new(AgentStep, $"Have {agentModel} read the code",
                $"The SAST agent explores the code read-only, in up to {parameters.AgentSteps} steps, and reports vulnerabilities the rules cannot see. " +
                $"Its findings are a model's candidates and do not change compliance results. {sentTo}"));
        }
        if (agents.Contains(ReviewAgents.ThreatModel))
        {
            steps.Add(new(ThreatModelStep, $"Have {agentModel} build a threat model",
                $"The threat-model agent maps the code's components and the threats against them, read-only, in up to {parameters.AgentSteps} steps. " +
                $"A hypothesis for people to review. {sentTo}"));
        }

        foreach (var id in parameters.Catalogs)
        {
            var catalog = catalogs.Get(id);
            steps.Add(new(AssessStep(id), $"Assess against {catalog?.Name ?? id}",
                $"{catalog?.ControlCount ?? 0} controls; those the scanner can judge get a verdict, the rest are left for you."));
            if (agents.Contains(ReviewAgents.Evidence))
            {
                steps.Add(new(EvidenceStep(id), $"Have {agentModel} look for evidence for {catalog?.Name ?? id}",
                    $"The evidence agent suggests answers for up to {Agents.EvidenceAgent.DefaultControls} controls the scanner could not judge. " +
                    $"Suggestions change nothing until you accept them. {sentTo}"));
            }
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
    Agents.ThreatModelAgentRunner threatModels, Agents.EvidenceAgentRunner evidence,
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
        var agents = parameters.AgentsToRun;
        var agentModel = parameters.AgentModel ?? "";
        AgentRun? agentRun = null;
        if (agents.Contains(ReviewAgents.Sast))
        {
            await reporter.StepAsync(ReviewPlan.AgentStep, Step(ReviewPlan.AgentStep).Title, StepStatus.Running);
            agentRun = await sast.RunAsync(job, new Agents.SastJobParameters(agentModel, parameters.AgentSteps), inner, ct);
            await reporter.StepAsync(ReviewPlan.AgentStep, Step(ReviewPlan.AgentStep).Title, StepStatus.Completed);
        }

        // The threat-model agent (optional)
        AgentRun? threatModelRun = null;
        if (agents.Contains(ReviewAgents.ThreatModel))
        {
            await reporter.StepAsync(ReviewPlan.ThreatModelStep, Step(ReviewPlan.ThreatModelStep).Title, StepStatus.Running);
            threatModelRun = await threatModels.RunAsync(job, new Agents.ThreatModelJobParameters(agentModel, parameters.AgentSteps), inner, ct);
            await reporter.StepAsync(ReviewPlan.ThreatModelStep, Step(ReviewPlan.ThreatModelStep).Title, StepStatus.Completed);
        }

        // 4. One assessment per catalog, all from the same scan
        var results = new List<AssessmentRun>();
        foreach (var id in parameters.Catalogs)
        {
            var catalog = catalogs.Get(id) ?? throw new InvalidOperationException($"Catalog '{id}' is no longer installed.");
            var stepId = ReviewPlan.AssessStep(id);
            await reporter.StepAsync(stepId, Step(stepId).Title, StepStatus.Running);
            var assessed = await assessments.RunAsync(job.UserId, job.EngagementId, catalog, scan.Scan, job.TargetPath, parameters.Sca, ct, inner);
            results.Add(assessed);
            await reporter.StepAsync(stepId, Step(stepId).Title, StepStatus.Completed);

            // The evidence agent (optional): suggested answers for what the engine could not judge
            if (agents.Contains(ReviewAgents.Evidence))
            {
                var evidenceStep = ReviewPlan.EvidenceStep(id);
                await reporter.StepAsync(evidenceStep, Step(evidenceStep).Title, StepStatus.Running);
                await evidence.RunAsync(job,
                    new Agents.EvidenceJobParameters(assessed.Assessment.Id, agentModel, MaxSteps: parameters.AgentSteps), inner, ct);
                await reporter.StepAsync(evidenceStep, Step(evidenceStep).Title, StepStatus.Completed);
            }
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
            ThreatModelRunId = threatModelRun?.Id,
            FindingCount = scan.Scan.FindingCount,
            Report = ReviewReport.Write(job.TargetPath, scan, results, opinions, DateTime.UtcNow, agentRun, threatModelRun,
                await FindingDecisions.ForUserAsync(db, job.UserId, ct)),
        };
        db.Reviews.Add(review);
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(job.UserId, "review.complete", AuditOutcome.Ok,
            $"Review {review.Id}: scan {scan.Scan.Id} ({scan.Scan.FindingCount} findings), {results.Count} assessment(s), {opinions.Count} model opinion(s){(agentRun is null ? "" : $", agent run {agentRun.Id} ({agentRun.FindingCount} reported)")}{(threatModelRun is null ? "" : $", threat model {threatModelRun.Id} ({threatModelRun.FindingCount} threats)")}.",
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
    private const int MaxThreatsListed = 10;

    /// <summary>How many lines apart a scanner finding and a model finding may be and still count as the same place.</summary>
    private const int CorrelationLines = 2;

    public static string Write(
        string targetPath, ScanRun scan, IReadOnlyList<AssessmentRun> assessments,
        IReadOnlyList<TriageResult> opinions, DateTime now, AgentRun? agentRun = null, AgentRun? threatModelRun = null,
        IReadOnlyDictionary<string, FindingDecision>? decisions = null)
    {
        // What the operator has already decided about these findings, in earlier scans or this one.
        FindingDecision? DecisionOn(Finding f) =>
            decisions is not null && decisions.TryGetValue(FindingDecisions.Fingerprint(f), out var d) ? d : null;
        var active = scan.Findings.Where(f => !f.Suppressed).ToList();
        var production = active.Where(f => f.Context is null).ToList();

        // Correlation: where the scanner and the model point at the same place, each says so of the other.
        var reported = agentRun is null ? [] : Agents.SastAgentRunner.ReadFindings(agentRun.Findings);
        var prefix = ScopePolicy.Normalize(TargetType.Path, targetPath) ?? "";
        string FromRoot(string file) => prefix.Length == 0 ? file : $"{prefix}/{file}";
        bool SamePlace(Finding scanner, Agents.AgentFinding model) =>
            scanner.File == FromRoot(model.File) && Math.Abs(scanner.Line - model.Line) <= CorrelationLines;
        // The model's finding against everything the scanner saw there — also test code, also what the target suppressed.
        string ScannerSaw(Agents.AgentFinding model)
        {
            var same = scan.Findings.Where(f => SamePlace(f, model)).ToList();
            return same.Count == 0 ? "model only" : same.Any(f => !f.Suppressed) ? "flagged it too" : "flagged it, but the target suppressed it";
        }
        var alsoFlagged = reported.Count(m => ScannerSaw(m) != "model only");
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
        var decided = production.Select(DecisionOn).Where(d => d is not null).ToList();
        if (decided.Count > 0)
        {
            text.AppendLine($"- **Your decisions:** {decided.Count} of the {production.Count} production findings already have one " +
                            $"({decided.Count(d => d!.Status == DecisionStatuses.AcceptedRisk)} accepted risk, {decided.Count(d => d!.Status == DecisionStatuses.FalsePositive)} false positive); {production.Count - decided.Count} still open");
        }
        if (agentRun is not null)
        {
            text.AppendLine($"- **Reported by the model:** {reported.Count} ({alsoFlagged} at places the scanner also flagged, {reported.Count - alsoFlagged} the model's alone)");
        }
        if (threatModelRun is not null)
        {
            text.AppendLine($"- **Threat model:** {threatModelRun.FindingCount} threat(s) recorded by the model");
        }
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

            var suggested = assessments
                .Select(a => (a.Report.CatalogName, Suggestions: Agents.EvidenceAgent.ReadSuggestions(a.Assessment).Values.ToList()))
                .Where(x => x.Suggestions.Count > 0).ToList();
            if (suggested.Count > 0)
            {
                text.AppendLine("A model looked for evidence for controls left for manual review and suggested answers. They change nothing until you accept them in the assessment:");
                text.AppendLine();
                foreach (var (catalogName, suggestions) in suggested)
                {
                    var counts = suggestions.GroupBy(s => s.Suggestion).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} \"{g.Key}\"");
                    text.AppendLine($"- **{catalogName}:** {suggestions.Count} suggestion(s) by `{suggestions[0].Model}` — {string.Join(", ", counts)}");
                }
                text.AppendLine();
            }

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
            var withModel = agentRun is not null;
            var withDecisions = decided.Count > 0;
            text.AppendLine("| Severity | Finding | Location | Model review |" + (withModel ? " Model reported it too |" : "") + (withDecisions ? " Your decision |" : ""));
            text.AppendLine("|---|---|---|---|" + (withModel ? "---|" : "") + (withDecisions ? "---|" : ""));
            // Open findings before decided ones; then most severe first; among equals, what the model also reported comes first.
            foreach (var f in production.OrderBy(f => DecisionOn(f) is null ? 0 : 1).ThenByDescending(f => f.Severity)
                         .ThenByDescending(f => reported.Any(m => SamePlace(f, m))).Take(MaxFindingsListed))
            {
                var verdict = verdicts.TryGetValue(f.Id, out var v)
                    ? v switch { TriageVerdict.TruePositive => "real", TriageVerdict.FalsePositive => "harmless", _ => "unsure" }
                    : "—";
                text.AppendLine($"| {f.Severity} | {Cell(f.Title)} | `{Cell(f.File)}:{f.Line}` | {verdict} |" +
                                (withModel ? $" {(reported.Any(m => SamePlace(f, m)) ? "yes" : "—")} |" : "") +
                                (withDecisions ? $" {(DecisionOn(f) is { } d ? $"{DecisionStatuses.Label(d.Status)}: {Cell(Shorten(d.Note, 120))}" : "open")} |" : ""));
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
                text.AppendLine("| Severity | Finding | Location | Scanner | Why |");
                text.AppendLine("|---|---|---|---|---|");
                foreach (var f in reported.OrderByDescending(f => f.Severity).Take(MaxFindingsListed))
                {
                    text.AppendLine($"| {f.Severity} | {Cell(f.Title)} | `{Cell(FromRoot(f.File))}:{f.Line}` | {ScannerSaw(f)} | {Cell(Shorten(f.Description, 240))} |");
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

        if (threatModelRun is not null)
        {
            var model = Agents.ThreatModelAgentRunner.Read(threatModelRun.Findings);
            string[] riskOrder = ["critical", "high", "medium", "low"];
            text.AppendLine($"## Threat model (`{threatModelRun.Model}`)");
            text.AppendLine();
            text.AppendLine("> A model's hypothesis from reading the code, for people who know the system to review; nothing here was tested." +
                            (threatModelRun.Complete ? "" : " **This part of the review did not complete; the model is partial.**"));
            text.AppendLine();
            if (model.Threats.Count == 0)
            {
                text.AppendLine($"{model.Components.Count} component(s) recorded, no threats.");
            }
            else
            {
                text.AppendLine($"{model.Components.Count} component(s), {model.Threats.Count} threat(s): " +
                                string.Join(", ", riskOrder.Select(r => (Risk: r, Count: model.Threats.Count(t => t.Risk == r))).Where(x => x.Count > 0).Select(x => $"{x.Count} {x.Risk}")) + ".");
                text.AppendLine();
                text.AppendLine("| Risk | Threat | STRIDE | Component |");
                text.AppendLine("|---|---|---|---|");
                foreach (var t in model.Threats.OrderBy(t => Array.IndexOf(riskOrder, t.Risk)).Take(MaxThreatsListed))
                {
                    text.AppendLine($"| {t.Risk} | {Cell(t.Title)} | {t.Category} | {Cell(t.Component)} |");
                }
                if (model.Threats.Count > MaxThreatsListed)
                {
                    text.AppendLine();
                    text.AppendLine($"…and {model.Threats.Count - MaxThreatsListed} more.");
                }
            }
            text.AppendLine();
            text.AppendLine($"The full threat model, with descriptions and mitigations, is agent run `{threatModelRun.Id}`.");
            text.AppendLine();
        }

        text.AppendLine("## What to do next");
        text.AppendLine();
        text.AppendLine("1. Review the findings above, most severe first; fix or record why each is acceptable." +
                        (agentRun is not null ? " Start with those both the scanner and the model point at." : ""));
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
