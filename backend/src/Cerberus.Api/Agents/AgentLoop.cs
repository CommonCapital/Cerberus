using System.Text;
using System.Text.Json;
using Cerberus.Core.Models;
using Cerberus.Jobs;
using Cerberus.Scanning;

namespace Cerberus.Api.Agents;

/// <summary>A tool an agent may call. It runs on the server, inside the agent's job.</summary>
public interface IAgentTool
{
    ToolDefinition Definition { get; }

    /// <summary>
    /// Runs the tool. The returned text goes back to the model. Throw
    /// <see cref="AgentToolException"/> for a problem the model can correct
    /// (bad input, no such file); it is reported to the model as a failed call.
    /// </summary>
    Task<string> RunAsync(JsonElement input, CancellationToken ct);

    /// <summary>What a call does besides reading. Declared here, in code — never taken from a model or from an outside server.</summary>
    ToolEffects Effects => ToolEffects.None;

    /// <summary>
    /// The kind of action (<c>ActionKinds</c>) the operator is asked about before each call — or null, for a
    /// tool whose calls run without asking: it only reads, or the run's own approval already covers it.
    /// </summary>
    string? AskFirst => null;

    /// <summary>For the operator, when asked: what this call would do, to complete "The agent wants to …".</summary>
    string Describe(JsonElement input) => $"use the tool \"{Definition.Name}\"";
}

public sealed class AgentToolException(string message) : Exception(message);

/// <param name="HitStepLimit">The agent was stopped at its step limit while still asking for tools.</param>
/// <param name="StoppedBecause">Set when the loop guard ended the run early: why, as "it …" (see <see cref="LoopGuard"/>).</param>
public sealed record AgentOutcome(
    string FinalText, int Steps, int ToolCalls, bool HitStepLimit, int? InputTokens, int? OutputTokens, string? StoppedBecause = null)
{
    /// <summary>The run was cut short, by its step limit or by the loop guard; what it produced may be incomplete.</summary>
    public bool StoppedEarly => HitStepLimit || StoppedBecause is not null;

    /// <summary>What the operator is told about a run that was cut short, or null for one that ended by itself.</summary>
    public string? StopNote(int maxSteps, string consequence) =>
        StoppedBecause is { } why ? $"The agent was stopped: {why}; {consequence}."
        : HitStepLimit ? $"The agent was stopped at its limit of {maxSteps} steps; {consequence}."
        : null;
}

/// <summary>
/// The server-side agent loop (PLAN.md §6): call the model, run the tools it
/// asks for, give it the results, repeat until it answers without tools or
/// reaches its step limit. Everything it does is put on the run's stream —
/// its text as it is written, and every tool call with its input and result
/// (§6.5, transparent tool use).
/// </summary>
public sealed class AgentLoop(IModelProviderResolver providers)
{
    /// <summary>How much of a tool result is shown on the stream; the model gets all of it.</summary>
    public const int MaxResultShown = 2000;

    public async Task<AgentOutcome> RunAsync(
        string model, ModelCredentials credentials, string system, string task,
        IReadOnlyList<IAgentTool> tools, int maxSteps, IRunReporter reporter, CancellationToken ct,
        Func<IReadOnlyList<string>>? operatorMessages = null, LoopGuard? guard = null, IToolGate? gate = null)
    {
        guard ??= new LoopGuard(LoopGuard.BudgetFor(maxSteps));
        var provider = providers.Resolve(model);
        var definitions = tools.Select(t => t.Definition).ToList();
        var turns = new List<ChatTurn> { ChatTurn.User(task) };
        var finalText = "";
        var toolCalls = 0;
        int? inputTokens = null, outputTokens = null;

        for (var step = 1; ; step++)
        {
            ct.ThrowIfCancellationRequested();
            // Steering: what the operator said since the last step is read before the model is called again.
            if (operatorMessages?.Invoke() is { Count: > 0 } said) turns.Add(ChatTurn.User(OperatorTurn(said)));
            var channel = $"agent-{step}";
            var text = new StringBuilder();
            var reasoning = new StringBuilder();
            var parts = new List<ChatPart>();
            var calls = new List<ToolCallComplete>();

            await foreach (var e in provider.StreamChatAsync(new ChatRequest(model, system, turns, definitions, credentials), ct))
            {
                switch (e)
                {
                    case TextDelta delta when delta.Text.Length > 0:
                        text.Append(delta.Text);
                        await reporter.TextAsync(channel, delta.Text);
                        break;
                    case ReasoningDelta delta:
                        reasoning.Append(delta.Text);
                        break;
                    case ReasoningEnd end:
                        // Kept only to be handed back to the provider with the next request.
                        parts.Add(new ReasoningPart(reasoning.ToString(), end.Signature, end.RedactedData));
                        reasoning.Clear();
                        break;
                    case ToolCallComplete call:
                        calls.Add(call);
                        break;
                    case RetryScheduled retry:
                        await reporter.EventAsync(RunEventTypes.Note, new
                        {
                            message = $"The model provider is busy ({retry.Reason}); trying again in {Math.Ceiling(retry.Delay.TotalSeconds)} s (attempt {retry.Attempt} of {retry.MaxAttempts}).",
                        });
                        break;
                    case UsageReport usage:
                        if (usage.InputTokens is { } i) inputTokens = (inputTokens ?? 0) + i;
                        if (usage.OutputTokens is { } o) outputTokens = (outputTokens ?? 0) + o;
                        break;
                }
            }
            await reporter.FlushTextAsync();

            if (text.Length > 0)
            {
                parts.Add(new TextPart(text.ToString()));
                finalText = text.ToString();
            }
            if (calls.Count == 0)
            {
                // About to finish — unless the operator has just said something and there is a step left to act on it.
                if (step < maxSteps && operatorMessages?.Invoke() is { Count: > 0 } late)
                {
                    if (parts.Count > 0) turns.Add(new ChatTurn("assistant", parts));
                    turns.Add(ChatTurn.User(OperatorTurn(late)));
                    continue;
                }
                return new AgentOutcome(finalText.Trim(), step, toolCalls, HitStepLimit: false, inputTokens, outputTokens);
            }

            var results = new List<ChatPart>();
            var useful = false;
            foreach (var call in calls)
            {
                ct.ThrowIfCancellationRequested();
                toolCalls++;
                var (input, output, failed) = await RunToolAsync(tools, call, guard, gate, reporter, ct);
                useful |= !failed;
                parts.Add(new ToolCallPart(call.Id, call.Name, input));
                results.Add(new ToolResultPart(call.Id, call.Name, output, failed));
            }
            guard.StepDone(useful);
            turns.Add(new ChatTurn("assistant", parts));
            turns.Add(new ChatTurn("tool", results));

            if (step >= maxSteps || guard.StopReason is not null)
            {
                // Out of steps, or stopped by the guard, while still working. One last call, without tools,
                // so the run ends with an account of what was done and what was not — rather than mid-thought.
                turns.Add(ChatTurn.User(guard.StopReason is { } reason ? LoopGuard.ClosingTurn(reason) : StepLimitTurn));
                var closing = new StringBuilder();
                await foreach (var e in provider.StreamChatAsync(new ChatRequest(model, system, turns, [], credentials), ct))
                {
                    switch (e)
                    {
                        case TextDelta delta when delta.Text.Length > 0:
                            closing.Append(delta.Text);
                            await reporter.TextAsync($"agent-{step}-closing", delta.Text);
                            break;
                        case UsageReport usage:
                            if (usage.InputTokens is { } i) inputTokens = (inputTokens ?? 0) + i;
                            if (usage.OutputTokens is { } o) outputTokens = (outputTokens ?? 0) + o;
                            break;
                    }
                }
                await reporter.FlushTextAsync();
                if (closing.Length > 0) finalText = closing.ToString();
                return new AgentOutcome(finalText.Trim(), step, toolCalls, HitStepLimit: guard.StopReason is null, inputTokens, outputTokens, guard.StopReason);
            }
        }
    }

    /// <summary>What the model is told when it has used up its steps.</summary>
    public const string StepLimitTurn =
        "You have reached your step limit and cannot call any more tools. Reply now with a short summary: " +
        "what you did, what you found or recorded, and what you did not get to.";

    /// <summary>How the operator's words reach the model: marked as theirs, and as taking priority.</summary>
    public static string OperatorTurn(IReadOnlyList<string> messages) =>
        "Message from the operator, sent while you were working. Follow it; it takes priority over what you had planned:\n\n" +
        string.Join("\n\n", messages);

    private static async Task<(JsonElement Input, string Output, bool Failed)> RunToolAsync(
        IReadOnlyList<IAgentTool> tools, ToolCallComplete call, LoopGuard guard, IToolGate? gate, IRunReporter reporter, CancellationToken ct)
    {
        JsonElement input;
        string? problem = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.InputJson) ? "{}" : call.InputJson);
            input = doc.RootElement.Clone();
            if (input.ValueKind != JsonValueKind.Object) problem = "The tool input is not a JSON object.";
        }
        catch (JsonException)
        {
            input = JsonDocument.Parse("{}").RootElement.Clone();
            problem = "The tool input is not valid JSON.";
        }

        var tool = tools.FirstOrDefault(t => t.Definition.Name == call.Name);
        if (problem is null && tool is null) problem = $"There is no tool named \"{call.Name}\".";
        // Every call counts against the run's limits — a malformed or unknown one too. A refused call is not run.
        var refusal = guard.Refuse(call.Name, input);
        problem ??= refusal;

        // The stream is stored and pushed to clients; the model gets the real thing, the record a masked copy.
        await reporter.EventAsync(RunEventTypes.ToolCall, new
        {
            id = call.Id,
            name = call.Name,
            input = JsonSerializer.Deserialize<JsonElement>(SecretMasker.MaskText(input.GetRawText())),
        });

        // The interrupt: a consequential call is put to the operator first and runs only on their yes. It comes
        // after the call has been shown on the stream, so the question stands next to what it is about. A run
        // that has nobody to ask does not make such a call at all.
        if (problem is null && tool!.AskFirst is not null)
        {
            var decision = gate is null
                ? new GateDecision(false, "This run has nobody to ask, and this call needs the operator's consent: it was not made. Go on without it.")
                : await gate.DecideAsync(tool, input, ct);
            if (!decision.Allowed) problem = decision.Reason ?? "The operator did not allow this call.";
        }

        string output;
        var failed = problem is not null;
        if (problem is not null)
        {
            output = problem;
        }
        else
        {
            try
            {
                output = await tool!.RunAsync(input, ct);
            }
            catch (AgentToolException ex)
            {
                // Told to the model as a failed call, so it can correct itself on the next step.
                output = ex.Message;
                failed = true;
            }
        }

        await reporter.EventAsync(RunEventTypes.ToolResult, new
        {
            id = call.Id,
            name = call.Name,
            output = SecretMasker.MaskText(output.Length <= MaxResultShown ? output : output[..MaxResultShown]),
            truncated = output.Length > MaxResultShown,
            isError = failed,
        });
        return (input, output, failed);
    }
}
