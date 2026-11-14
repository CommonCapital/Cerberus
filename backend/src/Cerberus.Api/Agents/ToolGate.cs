using System.Text.Json;
using Cerberus.Api.Audit;
using Cerberus.Authorization;
using Cerberus.Core.Models;
using Cerberus.Data;
using Cerberus.Jobs;
using Cerberus.Scanning;

namespace Cerberus.Api.Agents;

/// <summary>
/// What a tool call does besides reading (PLAN.md §22 item 2). Declared by the tool — in code, by whoever
/// wrote it — and never by the model or by an outside server about itself.
/// </summary>
[Flags]
public enum ToolEffects
{
    /// <summary>It only reads, inside what the run was given.</summary>
    None = 0,

    /// <summary>It keeps something in this system (a finding, a note).</summary>
    Records = 1,

    /// <summary>It sends something outside this machine.</summary>
    SendsOut = 2,

    /// <summary>It may change something in a system outside this one.</summary>
    ChangesOutside = 4,

    /// <summary>It creates or changes files.</summary>
    WritesFiles = 8,

    /// <summary>It starts a process.</summary>
    LaunchesProcess = 16,
}

/// <param name="Reason">When not allowed: what the model is told, in this system's words.</param>
public sealed record GateDecision(bool Allowed, string? Reason = null)
{
    public static GateDecision Yes { get; } = new(true);
}

/// <summary>Decides whether a call that needs consent may run. The agent loop asks it before every such call.</summary>
public interface IToolGate
{
    Task<GateDecision> DecideAsync(IAgentTool tool, JsonElement input, CancellationToken ct);
}

/// <summary>
/// The interrupt (PLAN.md §22 item 2): a consequential tool call does not run on a model's say-so. The run
/// stops and puts the call to the operator through its ordinary question — what the agent wants to do, with
/// what — and goes on with the answer: run it once, run this tool for the rest of the run, or not at all,
/// with the operator's reason passed to the model. Nobody there to answer means no.
/// <para>
/// Consent given beforehand is the same "always allow" as everywhere else: a kind of action the operator's
/// permissions always allow is not asked about, and the run says so. Every decision is in the audit log.
/// </para>
/// </summary>
public sealed class OperatorGate(Job job, CerberusDbContext db, AuditLog audit, IRunReporter reporter) : IToolGate
{
    public const string AllowOnce = "Allow this once";
    public const string AllowForRun = "Allow for the rest of this run";
    public const string Deny = "Do not allow";
    public const int MaxInputShown = 600;

    private readonly HashSet<string> _allowedForRun = [];
    private readonly HashSet<string> _saidNotAsked = [];

    public async Task<GateDecision> DecideAsync(IAgentTool tool, JsonElement input, CancellationToken ct)
    {
        var kind = tool.AskFirst ?? throw new InvalidOperationException("Only a tool that asks first comes to the gate.");
        var name = tool.Definition.Name;
        if (_allowedForRun.Contains(name)) return GateDecision.Yes;

        var (permissions, scope) = await PermissionEndpoints.ResolveAsync(db, job.UserId, job.EngagementId, ct);
        if (!permissions.NeedsApproval(kind))
        {
            if (_saidNotAsked.Add(name))
            {
                await reporter.EventAsync(RunEventTypes.Note, new { message = $"Not asked about {name}: \"{kind}\" is always allowed by the {scope} permissions." });
                await audit.AppendAsync(job.UserId, "tool.auto", AuditOutcome.Allowed,
                    $"Job {job.Id}: calls of {name} run without asking; \"{kind}\" is always allowed by the {scope} permissions.",
                    job.EngagementId, job.TargetPath, CapabilityTier.T0, ct);
            }
            return GateDecision.Yes;
        }

        // What is shown and kept is masked: a model's arguments may quote what it read.
        var shown = SecretMasker.MaskText(input.GetRawText());
        if (shown.Length > MaxInputShown) shown = shown[..MaxInputShown] + "…";
        var question = new OperatorQuestionSpec(
            $"The agent wants to {tool.Describe(input)}.\n\nTool: {name}\nWith: {shown}\n\n" +
            $"This {InWords(tool.Effects)}. It does not happen unless you allow it.",
            [
                new QuestionOption(AllowOnce, "This call runs; the next one asks again."),
                new QuestionOption(AllowForRun, "This tool is not asked about again in this run."),
                new QuestionOption(Deny, "The call is not made. Add why, or what to do instead, and the agent is told."),
            ],
            AllowText: true);
        var answer = await reporter.AskAsync(question, ct);

        if (answer is null)
        {
            await audit.AppendAsync(job.UserId, "tool.unanswered", AuditOutcome.Denied,
                $"Job {job.Id}: nobody allowed a call of {name}; it was not made.", job.EngagementId, job.TargetPath, CapabilityTier.T0, ct);
            return new GateDecision(false, "Nobody was there to allow this call, so it was not made. Go on without it, and say in your answer that it was not done.");
        }

        var allowed = answer.Selected.Contains(AllowOnce) || answer.Selected.Contains(AllowForRun);
        if (allowed && answer.Selected.Contains(AllowForRun)) _allowedForRun.Add(name);
        await audit.AppendAsync(job.UserId, allowed ? "tool.allowed" : "tool.denied", allowed ? AuditOutcome.Allowed : AuditOutcome.Denied,
            $"Job {job.Id}: the operator {(allowed ? "allowed" : "did not allow")} a call of {name}" +
            $"{(answer.Selected.Contains(AllowForRun) ? " (and this tool for the rest of the run)" : "")}{(answer.Text is null ? "" : ", with a comment")}.",
            job.EngagementId, job.TargetPath, CapabilityTier.T0, ct);
        if (allowed) return GateDecision.Yes;

        return new GateDecision(false, answer.Text is { Length: > 0 } why
            ? $"The operator did not allow this call and writes: \"{why}\". Do not try it again; do as they say, or go on without it."
            : "The operator did not allow this call. Do not try it again; go on without it, and say in your answer that it was not done.");
    }

    /// <summary>The effects as the operator reads them: "sends something outside this machine and may change something there".</summary>
    public static string InWords(ToolEffects effects)
    {
        var parts = new List<string>();
        if (effects.HasFlag(ToolEffects.SendsOut)) parts.Add("sends something outside this machine");
        if (effects.HasFlag(ToolEffects.ChangesOutside)) parts.Add("may change something in an outside system");
        if (effects.HasFlag(ToolEffects.WritesFiles)) parts.Add("creates or changes files");
        if (effects.HasFlag(ToolEffects.LaunchesProcess)) parts.Add("starts a process");
        if (effects.HasFlag(ToolEffects.Records)) parts.Add("keeps something in this system");
        return parts.Count switch
        {
            0 => "is marked as needing your consent",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
    }
}

/// <summary>
/// Lets an agent stop and ask instead of guessing (PLAN.md §22 item 2). The question goes to the operator
/// through the run's ordinary question; the run waits, its time limit paused. Offered only where the run was
/// started by somebody who said a person is there: an agent in a pipeline or on a schedule does not get it.
/// A few questions a run at most, so that an agent cannot hand its work back one question at a time.
/// </summary>
public sealed class AskOperatorTool(IRunReporter reporter) : IAgentTool
{
    public const string Name = "askOperator";
    public const int MaxQuestions = 3;
    public const int MaxQuestionLength = 600;
    public const int MaxOptions = 6;
    public const int MaxOptionLength = 80;

    public static readonly string Hint =
        "If something you need cannot be found in the code — which environment is meant, whether something is known and accepted, what matters most to them — " +
        $"you may ask the operator with {Name}. Rarely: at most {MaxQuestions} questions, and never for what you can read yourself. " +
        "They may be away; then go on with the safest assumption and say what you assumed.";

    private int _asked;

    public ToolDefinition Definition { get; } = CodeTools.Define(Name,
        "Ask the operator one question and wait for the answer. For what the code cannot tell you: which environment is meant, whether something is known " +
        "and accepted, what to look at first. Offer a few short options where you can; they may also answer in their own words. They may be away — you then " +
        $"get no answer and go on with the safest assumption. At most {MaxQuestions} questions per run. Do not put code or secrets in the question.",
        """
        {"type":"object","properties":{
          "question":{"type":"string","description":"One question, self-contained, at most 600 characters"},
          "options":{"type":"array","items":{"type":"string"},"description":"Up to 6 short answers to choose from (optional)"}
        },"required":["question"],"additionalProperties":false}
        """);

    public async Task<string> RunAsync(JsonElement input, CancellationToken ct)
    {
        var question = CodeTools.Text(input, "question")?.Trim();
        if (string.IsNullOrEmpty(question)) throw new AgentToolException("question is required.");
        if (question.Length > MaxQuestionLength) throw new AgentToolException($"Keep the question under {MaxQuestionLength} characters.");

        var options = new List<QuestionOption>();
        if (input.TryGetProperty("options", out var given) && given.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in given.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(option.GetString())) continue;
                var label = SecretMasker.MaskText(option.GetString()!.Trim());
                options.Add(new QuestionOption(label.Length <= MaxOptionLength ? label : label[..MaxOptionLength]));
            }
            if (options.Count > MaxOptions) throw new AgentToolException($"Offer at most {MaxOptions} options.");
        }

        if (_asked >= MaxQuestions)
        {
            throw new AgentToolException($"You have asked your {MaxQuestions} questions in this run. Go on with what you know and say what you assumed.");
        }

        // A model may quote what it read: what is shown to a person and kept is masked.
        var spec = new OperatorQuestionSpec(SecretMasker.MaskText(question), options, AllowText: true);
        if (OperatorQuestions.ValidateSpec(spec) is { } problem) throw new AgentToolException(problem);
        _asked++;

        var answer = await reporter.AskAsync(spec, ct);
        if (answer is null)
        {
            return "Nobody answered. Go on with the safest assumption, and say in your summary what you assumed and that the question went unanswered.";
        }
        var chosen = string.Join(", ", answer.Selected);
        return "The operator answered: " + (chosen.Length > 0 ? chosen : "") +
               (answer.Text is { Length: > 0 } text ? (chosen.Length > 0 ? " — " : "") + text : "");
    }
}
