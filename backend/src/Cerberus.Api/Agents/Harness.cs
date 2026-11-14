using Cerberus.Api.Audit;
using Cerberus.Api.Chat;
using Cerberus.Api.Settings;
using Cerberus.Authorization;
using Cerberus.Core.Models;
using Cerberus.Data;
using Cerberus.Jobs;
using Cerberus.Scanning;
using Cerberus.Skills;
using Cerberus.Triage;

namespace Cerberus.Api.Agents;

/// <summary>What every agent may be given besides its own tools. An agent's description says which of these it takes.</summary>
[Flags]
public enum AgentOffers
{
    None = 0,

    /// <summary>The read-only tools over the code under review (<see cref="CodeTools"/>).</summary>
    Code = 1,

    /// <summary>Search by meaning, where the target has an index.</summary>
    CodeSearch = 2,

    /// <summary>The clean-room researcher — if the run was also started with research and the installation has it.</summary>
    Research = 4,

    /// <summary>A question to the operator — if the run was also started with a person there, and never for a specialist.</summary>
    Questions = 8,

    /// <summary>A note for later runs on the same target.</summary>
    Notes = 16,

    /// <summary>The skills library.</summary>
    Skills = 32,
}

/// <summary>
/// One agent, described (PLAN.md §22 item 3): everything about it that is not its own tools and not one
/// particular run. Adding an agent is one of these and an <see cref="IAgentWork"/>; the running is the
/// harness's, the same for all.
/// </summary>
/// <param name="Name">What its runs are stored under.</param>
/// <param name="StepId">The id of its step on the run's stream.</param>
/// <param name="System">Its instructions.</param>
/// <param name="StepLimit">The most steps a run of it may be given, whatever is asked for.</param>
/// <param name="IfCutShort">What a run that was stopped early means for its result ("the review may be incomplete").</param>
/// <param name="Called">What it is called in a note to the operator ("agent", "lead").</param>
/// <param name="Stored">Whether a run is kept as an <see cref="AgentRun"/>. An agent whose results live elsewhere keeps none.</param>
public sealed record AgentSpec(
    string Name, string StepId, string System, int StepLimit, string IfCutShort, AgentOffers Offers,
    string Called = "agent", bool Stored = true);

/// <summary>One run of an agent, as asked for.</summary>
/// <param name="TargetPath">The code it works on — the job's, or what the job points at.</param>
/// <param name="Research">The run was started with research on the internet allowed.</param>
/// <param name="Questions">The run was started by somebody who is there to be asked.</param>
/// <param name="Brief">Set when it works for a lead agent instead of on its own.</param>
public sealed record AgentRequest(
    Job Job, string Model, int MaxSteps, string TargetPath,
    bool Research = false, bool Questions = false, SpecialistBrief? Brief = null);

/// <summary>What an agent's own tools are built from, for one run.</summary>
/// <param name="RunId">The id the run will be stored under (also where nothing is stored: it names the run).</param>
/// <param name="SentTo">Where what the agent reads is sent, as the audit log says it.</param>
public sealed record AgentContext(
    Job Job, string Model, ModelProvider Provider, bool Local, CodeWorkspace Workspace, ModelCredentials Credentials,
    int MaxSteps, string TargetPath, string RunId, string SentTo, IRunReporter Reporter, CerberusDbContext Db, AuditLog Audit,
    SpecialistBrief? Brief);

/// <param name="Ok">Whether the run counts as having gone well.</param>
public sealed record AuditLine(string Action, bool Ok, string Text);

/// <summary>What is particular to one run of one agent: its task, its own tools, and what becomes of what it recorded.</summary>
public interface IAgentWork
{
    /// <summary>The step's title on the run's stream.</summary>
    string Title { get; }

    /// <summary>What the agent is asked to do. Hints for the shared tools it was given are added by the harness.</summary>
    string Assignment { get; }

    /// <summary>The agent's own tools — the ones it records its results with.</summary>
    IReadOnlyList<IAgentTool> Tools { get; }

    /// <summary>After the agent ended in the ordinary way, before its step is marked done.</summary>
    Task EndedAsync(AgentOutcome outcome) => Task.CompletedTask;

    /// <summary>Puts what the agent recorded into its stored run. Called for a run that broke off too: what it had is kept.</summary>
    void Fill(AgentRun run, AgentOutcome? outcome)
    {
    }

    /// <summary>What the audit log says about the run.</summary>
    AuditLine Audit(AgentRun? run, AgentOutcome? outcome);
}

/// <param name="Run">The stored run, where the agent keeps one.</param>
/// <param name="Outcome">How the loop ended.</param>
public sealed record AgentResult(AgentRun? Run, AgentOutcome Outcome);

/// <summary>
/// Runs any agent from its description (PLAN.md §22 item 3). What used to be written out once per agent is
/// here once: where the model is and whether what the agent reads leaves the machine; the confined view of
/// the code; the shared tools the description takes, each under its own condition; the operator's messages;
/// what was remembered about the target; the loop with its limits and its gate; the note when it was cut
/// short; the stored run, kept also when the run broke off; and the audit line.
/// <para>
/// Every agent therefore gets the same guarantees without asking for them: its step limit is capped by its
/// description, a call that needs consent goes to the operator (<see cref="OperatorGate"/>), a specialist
/// can neither take the operator's messages nor ask them anything, and what it wrote is masked before it is kept.
/// </para>
/// </summary>
public sealed class AgentHarness(
    AgentLoop loop, ApiKeyService keys, CerberusDbContext db, AuditLog audit, ScanOptions scanOptions, SkillLibrary skills,
    OperatorInbox inbox, Search.CodeSearch search, Web.ResearchTools research)
{
    public async Task<AgentResult> RunAsync(
        AgentSpec spec, AgentRequest request, Func<AgentContext, IAgentWork> build, IRunReporter reporter, CancellationToken ct)
    {
        var job = request.Job;
        var brief = request.Brief;
        var provider = ModelRegistry.Find(request.Model)?.Provider
            ?? throw new InvalidOperationException($"Unsupported model: {request.Model}");
        var local = FindingTriager.IsLocal(provider);
        // A model outside this machine gets the code with credential-shaped values masked.
        var workspace = new CodeWorkspace(scanOptions.ScanRoot, request.TargetPath, redact: !local);
        var credentials = await keys.ResolveCredentialsAsync(job.UserId, ct);
        var maxSteps = Math.Clamp(request.MaxSteps, 1, spec.StepLimit);

        var run = new AgentRun
        {
            UserId = job.UserId,
            EngagementId = job.EngagementId,
            JobId = job.Id,
            ParentRunId = brief?.ParentRunId,
            Agent = brief?.Agent ?? spec.Name,
            Model = request.Model,
            TargetPath = request.TargetPath,
        };
        var context = new AgentContext(
            job, request.Model, provider, local, workspace, credentials, maxSteps, request.TargetPath, run.Id,
            local ? "a local model" : $"{provider} (credential-shaped values masked)", reporter, db, audit, brief);
        var work = build(context);

        // The shared tools, each under its own condition. What an agent may have is its description's to say;
        // whether this run gets it is the request's — and a specialist never asks the operator anything.
        var semantic = spec.Offers.HasFlag(AgentOffers.CodeSearch) ? await search.ToolForAsync(job.UserId, request.TargetPath, ct) : null;
        var researcher = spec.Offers.HasFlag(AgentOffers.Research) && request.Research
            ? await research.ForAsync(job, request.Model, credentials, reporter, db, ct)
            : null;
        var asking = spec.Offers.HasFlag(AgentOffers.Questions) && request.Questions && brief is null ? new AskOperatorTool(reporter) : null;
        IReadOnlyList<IAgentTool> tools =
        [
            .. spec.Offers.HasFlag(AgentOffers.Code) ? CodeTools.For(workspace) : [],
            .. semantic is null ? Array.Empty<IAgentTool>() : [semantic],
            .. researcher is null ? Array.Empty<IAgentTool>() : [researcher],
            .. asking is null ? Array.Empty<IAgentTool>() : [asking],
            .. work.Tools,
            .. spec.Offers.HasFlag(AgentOffers.Notes) ? [new SastAgentRunner.RememberTool(db, audit, job)] : Array.Empty<IAgentTool>(),
            .. spec.Offers.HasFlag(AgentOffers.Skills)
                ? [new SastAgentRunner.SkillTool(ChatTools.SearchSkills, skills), new SastAgentRunner.SkillTool(ChatTools.LoadSkill, skills)]
                : Array.Empty<IAgentTool>(),
        ];

        var title = work.Title;
        await reporter.StepAsync(spec.StepId, title, StepStatus.Running);

        AgentOutcome? outcome = null;
        // A specialist is steered by its lead, not by the operator: the job's messages are the lead's to read.
        var messages = brief is null ? inbox.Open(job.Id) : null;
        try
        {
            var memory = await Memory.PromptSectionAsync(db, job.UserId, job.EngagementId, request.TargetPath, ct);
            outcome = await loop.RunAsync(
                request.Model, credentials,
                spec.System + (brief is null ? "" : "\n\n" + brief.Focus) + (memory.Length > 0 ? "\n\n" + memory : ""),
                work.Assignment +
                (semantic is null ? "" : "\n\n" + Search.CodeSearch.ToolHint) +
                (researcher is null ? "" : "\n\n" + Web.Research.ToolHint) +
                (asking is null ? "" : "\n\n" + AskOperatorTool.Hint),
                tools, maxSteps, reporter, ct, messages is null ? null : messages.Take,
                // A call that needs consent is put to the operator, whichever agent makes it.
                gate: new OperatorGate(job, db, audit, reporter));
            if (outcome.StopNote(maxSteps, spec.IfCutShort) is { } stopped)
            {
                await reporter.EventAsync(RunEventTypes.Note, new { message = stopped });
            }
            await work.EndedAsync(outcome);
            await reporter.StepAsync(spec.StepId, title, StepStatus.Completed);
        }
        finally
        {
            messages?.Dispose();
            if (messages is { Unread.Count: > 0 })
            {
                await reporter.EventAsync(RunEventTypes.Note, new { message = $"The {spec.Called} had finished before it could read your last {messages.Unread.Count} message(s)." });
            }
            // Kept even when stopped part-way: what was recorded so far is still worth having,
            // and code may already have left this machine.
            if (spec.Stored)
            {
                run.Summary = SecretMasker.MaskText(outcome?.FinalText ?? "");
                run.Steps = outcome?.Steps ?? 0;
                run.ToolCalls = outcome?.ToolCalls ?? 0;
                run.Complete = outcome is { StoppedEarly: false };
                work.Fill(run, outcome);
                db.AgentRuns.Add(run);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            var line = work.Audit(spec.Stored ? run : null, outcome);
            await audit.AppendAsync(job.UserId, line.Action, line.Ok ? AuditOutcome.Ok : AuditOutcome.Error, line.Text,
                job.EngagementId, request.TargetPath, CapabilityTier.T0, CancellationToken.None);
        }
        return new AgentResult(spec.Stored ? run : null, outcome);
    }
}
