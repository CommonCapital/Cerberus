using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cerberus.Core.Models;
using Cerberus.Skills;

namespace Cerberus.Api.Chat;

/// <param name="Memory">The operator's standing instructions and notes, as a prompt section; empty for none.</param>
/// <param name="Context">The client's environment and project instructions, as a prompt section; empty for none.</param>
/// <param name="Summary">A summary standing in for the messages up to <paramref name="SummarizedThrough"/>; null if the chat was never compacted.</param>
/// <param name="Notices">Transient notices to send the client as the reply starts (kind, data).</param>
/// <param name="Delegation">The client can run helper conversations: the agent is offered the task tool.</param>
/// <param name="Helper">This conversation is a helper's: it reports to another agent, not to the user.</param>
public sealed record ChatRun(
    string Mode, string Model, List<JsonObject> Messages, ModelCredentials Credentials, string Memory = "", string Context = "",
    string? Summary = null, string? SummarizedThrough = null, IReadOnlyList<(string Kind, object Data)>? Notices = null,
    string? Tasks = null, bool Delegation = false, bool Helper = false);

/// <param name="Messages">The conversation including the reply.</param>
/// <param name="Complete">
/// False when the reply ends with tool calls the client still has to run (it
/// will send the message back with their results), or when the model failed —
/// in both cases nothing is stored yet.
/// </param>
/// <param name="Tasks">The task list as the reply left it (JSON), or null if the reply did not change it.</param>
public sealed record ChatResult(List<JsonObject> Messages, bool Complete, string? Tasks = null);

/// <summary>
/// Produces one reply of the chat agent and streams it in the AI-SDK UI message
/// protocol. A reply is one or more model steps:
/// <list type="bullet">
/// <item>text and reasoning are streamed as they arrive;</item>
/// <item>calls to <b>server tools</b> (the skills library) are executed here and
/// the model is called again with the results, within the same reply;</item>
/// <item>calls to <b>client tools</b> (files, shell) end the reply — the CLI runs
/// them and resubmits the same assistant message, which this then continues.</item>
/// </list>
/// </summary>
public sealed class ChatRunner(IModelProviderResolver resolver, SkillLibrary skills)
{
    /// <summary>Upper bound on model calls within one reply, so a model cannot loop on server tools forever.</summary>
    public const int MaxSteps = 8;

    public async Task<ChatResult> RunAsync(UiMessageStreamWriter writer, ChatRun run, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var messages = run.Messages;

        // A resubmitted assistant message (its tool calls now have results) is continued, not answered anew.
        var last = messages[^1];
        var continuing = UiMessages.RoleOf(last) == "assistant";
        var messageId = (continuing ? UiMessages.IdOf(last) : null) ?? Guid.NewGuid().ToString("n");
        var parts = continuing ? (JsonArray)UiMessages.PartsOf(last).DeepClone() : [];

        // The model gets the summary of the compacted part and, in full, only what came after it.
        var turns = UiMessages.ToTurns(ChatCompactor.After(messages, run.Summary is null ? null : run.SummarizedThrough));
        var tools = ChatTools.For(run.Mode, run.Delegation && !run.Helper);
        // The agent's own task list: stored with the session, so it is there after a compaction and across requests.
        var taskList = new TaskList(run.Tasks);
        var system = string.Join("\n\n", new[]
        {
            SystemPrompt.Build(run.Mode), run.Helper ? SystemPrompt.Helper : "",
            run.Delegation && !run.Helper ? SystemPrompt.Delegating : "", run.Context, run.Memory,
            run.Summary is null ? "" : ChatCompactor.PromptSection(run.Summary),
            TaskList.PromptSection(taskList.Tasks),
        }.Where(part => part.Length > 0));
        var provider = resolver.Resolve(run.Model);

        int? inputTokens = null, outputTokens = null;
        var blockCounter = parts.Count;

        await writer.StartAsync(messageId, new { mode = run.Mode, model = run.Model }, ct);
        foreach (var (kind, data) in run.Notices ?? []) await writer.NoticeAsync(kind, data, ct);

        try
        {
            for (var stepNumber = 1; ; stepNumber++)
            {
                await writer.StartStepAsync(ct);
                parts.Add(new JsonObject { ["type"] = "step-start" });

                var step = new StepState();
                var request = new ChatRequest(run.Model, system, turns, tools, run.Credentials);

                await foreach (var e in provider.StreamChatAsync(request, ct))
                {
                    switch (e)
                    {
                        case TextDelta text when text.Text.Length > 0:
                            await CloseReasoningAsync(writer, step, parts, null, null, ct);
                            if (step.TextId is null)
                            {
                                step.TextId = $"t{blockCounter++}";
                                await writer.TextStartAsync(step.TextId, ct);
                            }
                            step.Text.Append(text.Text);
                            await writer.TextDeltaAsync(step.TextId, text.Text, ct);
                            break;

                        case ReasoningDelta reasoning:
                            await CloseTextAsync(writer, step, parts, ct);
                            if (step.ReasoningId is null)
                            {
                                step.ReasoningId = $"r{blockCounter++}";
                                await writer.ReasoningStartAsync(step.ReasoningId, ct);
                            }
                            if (reasoning.Text.Length > 0)
                            {
                                step.Reasoning.Append(reasoning.Text);
                                await writer.ReasoningDeltaAsync(step.ReasoningId, reasoning.Text, ct);
                            }
                            break;

                        case ReasoningEnd end:
                            await CloseTextAsync(writer, step, parts, ct);
                            if (step.ReasoningId is null)
                            {
                                // A block with no visible text still has to be kept: its signature goes back to the provider.
                                step.ReasoningId = $"r{blockCounter++}";
                                await writer.ReasoningStartAsync(step.ReasoningId, ct);
                            }
                            await CloseReasoningAsync(writer, step, parts, end.Signature, end.RedactedData, ct);
                            break;

                        case ToolCallStart start:
                            await CloseTextAsync(writer, step, parts, ct);
                            await CloseReasoningAsync(writer, step, parts, null, null, ct);
                            step.Announced.Add(start.Id);
                            await writer.ToolInputStartAsync(start.Id, start.Name, ChatTools.IsServerTool(start.Name), ct);
                            break;

                        case ToolCallInputDelta delta when delta.JsonFragment.Length > 0:
                            await writer.ToolInputDeltaAsync(delta.Id, delta.JsonFragment, ct);
                            break;

                        case ToolCallComplete call:
                            await CloseTextAsync(writer, step, parts, ct);
                            await CloseReasoningAsync(writer, step, parts, null, null, ct);
                            await HandleToolCallAsync(writer, step, parts, tools, call, taskList, ct);
                            break;

                        case UsageReport usage:
                            if (usage.InputTokens is { } i) inputTokens = (inputTokens ?? 0) + i;
                            if (usage.OutputTokens is { } o) outputTokens = (outputTokens ?? 0) + o;
                            break;

                        case RetryScheduled retry:
                            // The provider is busy: tell the client why nothing is arriving, and for how long.
                            await writer.NoticeAsync("retry", new
                            {
                                attempt = retry.Attempt,
                                maxAttempts = retry.MaxAttempts,
                                seconds = (int)Math.Ceiling(retry.Delay.TotalSeconds),
                                reason = retry.Reason,
                            }, ct);
                            break;
                    }
                }

                await CloseTextAsync(writer, step, parts, ct);
                await CloseReasoningAsync(writer, step, parts, null, null, ct);
                await writer.FinishStepAsync(ct);

                // The model is called again only when everything it asked for has been answered here.
                if (step.Calls.Count == 0 || step.WaitingForClient || stepNumber >= MaxSteps) break;

                turns.Add(new ChatTurn("assistant", [.. step.ModelParts]));
                turns.Add(new ChatTurn("tool", [.. step.Results]));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await writer.ErrorAsync(ex.Message, ct);
            await writer.DoneAsync(ct);
            return new ChatResult(messages, Complete: false, taskList.Changed ? taskList.Serialize() : null);
        }

        var durationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
        var metadata = new JsonObject { ["mode"] = run.Mode, ["model"] = run.Model, ["durationMs"] = durationMs };
        if (inputTokens is not null || outputTokens is not null)
        {
            metadata["usage"] = new JsonObject
            {
                ["inputTokens"] = inputTokens,
                ["outputTokens"] = outputTokens,
                ["totalTokens"] = (inputTokens ?? 0) + (outputTokens ?? 0),
            };
        }
        await writer.FinishAsync(metadata, ct);
        await writer.DoneAsync(ct);

        var reply = new JsonObject { ["id"] = messageId, ["role"] = "assistant", ["parts"] = parts, ["metadata"] = metadata.DeepClone() };
        if (continuing) messages[^1] = reply;
        else messages.Add(reply);

        return new ChatResult(messages, Complete: !UiMessages.HasPendingToolCalls(reply), taskList.Changed ? taskList.Serialize() : null);
    }

    private async Task HandleToolCallAsync(
        UiMessageStreamWriter writer, StepState step, JsonArray parts, IReadOnlyList<ToolDefinition> tools,
        ToolCallComplete call, TaskList taskList, CancellationToken ct)
    {
        var isServer = ChatTools.IsServerTool(call.Name);
        if (!step.Announced.Contains(call.Id))
        {
            await writer.ToolInputStartAsync(call.Id, call.Name, isServer, ct);
        }

        var part = new JsonObject { ["type"] = $"tool-{call.Name}", ["toolCallId"] = call.Id };
        if (isServer) part["providerExecuted"] = true;
        parts.Add(part);
        step.Calls.Add(call.Id);

        // The input must be a JSON object, and the tool must be one that was offered.
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
        if (problem is null && tools.All(t => t.Name != call.Name))
        {
            problem = $"There is no tool named \"{call.Name}\".";
        }

        if (problem is not null)
        {
            // Told to the model as a failed call, so it can correct itself on the next step.
            await writer.ToolInputErrorAsync(call.Id, call.Name, call.InputJson, problem, ct);
            part["state"] = "output-error";
            part["input"] = JsonValue.Create(call.InputJson);
            part["errorText"] = problem;
            step.ModelParts.Add(new ToolCallPart(call.Id, call.Name, JsonDocument.Parse("{}").RootElement.Clone()));
            step.Results.Add(new ToolResultPart(call.Id, call.Name, problem, IsError: true));
            return;
        }

        await writer.ToolInputAvailableAsync(call.Id, call.Name, input, isServer, ct);
        part["input"] = JsonNode.Parse(input.GetRawText());
        step.ModelParts.Add(new ToolCallPart(call.Id, call.Name, input));

        if (!isServer)
        {
            // The CLI runs it and resubmits this message with the result.
            part["state"] = "input-available";
            step.WaitingForClient = true;
            return;
        }

        try
        {
            var output = call.Name == ChatTools.UpdateTasks ? taskList.Update(input) : ChatTools.ExecuteServerTool(call.Name, input, skills);
            using var doc = JsonDocument.Parse(output);
            await writer.ToolOutputAvailableAsync(call.Id, doc.RootElement, ct);
            part["state"] = "output-available";
            part["output"] = JsonNode.Parse(output);
            step.Results.Add(new ToolResultPart(call.Id, call.Name, output));
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException)
        {
            await writer.ToolOutputErrorAsync(call.Id, ex.Message, ct);
            part["state"] = "output-error";
            part["errorText"] = ex.Message;
            step.Results.Add(new ToolResultPart(call.Id, call.Name, ex.Message, IsError: true));
        }
    }

    private static async Task CloseTextAsync(UiMessageStreamWriter writer, StepState step, JsonArray parts, CancellationToken ct)
    {
        if (step.TextId is null) return;
        await writer.TextEndAsync(step.TextId, ct);
        var text = step.Text.ToString();
        parts.Add(new JsonObject { ["type"] = "text", ["text"] = text, ["state"] = "done" });
        step.ModelParts.Add(new TextPart(text));
        step.TextId = null;
        step.Text.Clear();
    }

    private static async Task CloseReasoningAsync(
        UiMessageStreamWriter writer, StepState step, JsonArray parts, string? signature, string? redactedData,
        CancellationToken ct)
    {
        if (step.ReasoningId is null) return;

        JsonObject? providerMetadata = null;
        if (signature is not null || redactedData is not null)
        {
            var anthropic = new JsonObject();
            if (signature is not null) anthropic["signature"] = signature;
            if (redactedData is not null) anthropic["redactedData"] = redactedData;
            providerMetadata = new JsonObject { ["anthropic"] = anthropic };
        }

        await writer.ReasoningEndAsync(step.ReasoningId, providerMetadata, ct);
        var text = step.Reasoning.ToString();
        var part = new JsonObject { ["type"] = "reasoning", ["text"] = text, ["state"] = "done" };
        if (providerMetadata is not null) part["providerMetadata"] = providerMetadata.DeepClone();
        parts.Add(part);
        if (signature is not null || redactedData is not null)
        {
            step.ModelParts.Add(new ReasoningPart(text, signature, redactedData));
        }
        step.ReasoningId = null;
        step.Reasoning.Clear();
    }

    /// <summary>What one model step has produced so far.</summary>
    private sealed class StepState
    {
        public string? TextId;
        public readonly StringBuilder Text = new();
        public string? ReasoningId;
        public readonly StringBuilder Reasoning = new();
        public readonly HashSet<string> Announced = [];
        public readonly List<string> Calls = [];
        /// <summary>The step as the model must see it on the next call: its own text, reasoning and tool calls, in order.</summary>
        public readonly List<ChatPart> ModelParts = [];
        public readonly List<ChatPart> Results = [];
        public bool WaitingForClient;
    }
}
