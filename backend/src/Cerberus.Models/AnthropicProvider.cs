using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Cerberus.Core.Models;
using TextDelta = Cerberus.Core.Models.TextDelta;

namespace Cerberus.Models;

/// <summary>
/// Claude models through the official Anthropic C# SDK (Messages API,
/// streaming): text, tool use, and thinking. Thinking blocks are passed back
/// unchanged — with their signatures — when a conversation continues after a
/// tool call, as the API requires.
/// </summary>
public sealed class AnthropicProvider(HttpClient httpClient) : IModelProvider
{
    /// <summary>Streaming requests can afford a generous cap; a low one truncates tool inputs mid-JSON.</summary>
    private const long MaxTokens = 64000;

    public ModelProvider Provider => ModelProvider.Anthropic;

    public IAsyncEnumerable<string> StreamAsync(
        ChatCompletionRequest request, CancellationToken cancellationToken = default)
    {
        var turns = request.Messages
            .Select(m => new ChatTurn(m.Role == "assistant" ? "assistant" : "user", [new TextPart(m.Content)]))
            .ToList();
        return ChatEvents.TextOnly(
            StreamChatAsync(new ChatRequest(request.ModelId, request.System, turns, [], request.Credentials), cancellationToken),
            cancellationToken);
    }

    public async IAsyncEnumerable<ChatEvent> StreamChatAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var apiKey = request.Credentials.AnthropicApiKey
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "No Anthropic API key. Add one (/keys in the CLI), set ANTHROPIC_API_KEY, or use a local Ollama model.");
        }

        var client = new AnthropicClient { ApiKey = apiKey, HttpClient = httpClient };
        var parameters = BuildParameters(request);

        // Per content block (by index): what kind it is and what has accumulated.
        var tools = new Dictionary<long, (string Id, string Name, StringBuilder Json)>();
        var thinking = new Dictionary<long, string?>();   // index → signature, once received
        string? stopReason = null;

        await foreach (var streamEvent in client.Messages.CreateStreaming(parameters, cancellationToken))
        {
            if (streamEvent.TryPickStart(out var start))
            {
                yield return new UsageReport((int)start.Message.Usage.InputTokens, null);
            }
            else if (streamEvent.TryPickContentBlockStart(out var blockStart))
            {
                var block = blockStart.ContentBlock;
                if (block.TryPickToolUse(out var toolUse))
                {
                    tools[blockStart.Index] = (toolUse.ID, toolUse.Name, new StringBuilder());
                    yield return new ToolCallStart(toolUse.ID, toolUse.Name);
                }
                else if (block.TryPickThinking(out _))
                {
                    thinking[blockStart.Index] = null;
                    yield return new ReasoningDelta(string.Empty);   // opens the block even if its text is withheld
                }
                else if (block.TryPickRedactedThinking(out var redacted))
                {
                    yield return new ReasoningEnd(RedactedData: redacted.Data);
                }
            }
            else if (streamEvent.TryPickContentBlockDelta(out var blockDelta))
            {
                var delta = blockDelta.Delta;
                if (delta.TryPickText(out var text))
                {
                    yield return new TextDelta(text.Text);
                }
                else if (delta.TryPickInputJson(out var inputJson) && tools.TryGetValue(blockDelta.Index, out var tool))
                {
                    tool.Json.Append(inputJson.PartialJson);
                    yield return new ToolCallInputDelta(tool.Id, inputJson.PartialJson);
                }
                else if (delta.TryPickThinking(out var thought))
                {
                    yield return new ReasoningDelta(thought.Thinking);
                }
                else if (delta.TryPickSignature(out var signature))
                {
                    thinking[blockDelta.Index] = signature.Signature;
                }
            }
            else if (streamEvent.TryPickContentBlockStop(out var blockStop))
            {
                if (tools.TryGetValue(blockStop.Index, out var tool))
                {
                    yield return new ToolCallComplete(tool.Id, tool.Name, tool.Json.ToString());
                }
                else if (thinking.TryGetValue(blockStop.Index, out var signature))
                {
                    yield return new ReasoningEnd(Signature: signature);
                }
            }
            else if (streamEvent.TryPickDelta(out var messageDelta))
            {
                yield return new UsageReport(null, (int)messageDelta.Usage.OutputTokens);
                if (messageDelta.Delta.StopReason is { } reason) stopReason = reason.Raw();
            }
        }

        // A refusal is not an answer: say so instead of ending as if the reply were complete.
        if (stopReason == "refusal")
        {
            throw new InvalidOperationException("The model declined this request (stop reason: refusal).");
        }
        yield return new StepFinished(stopReason);
    }

    /// <summary>The request in the SDK's types. Public so its shape can be tested without a network call.</summary>
    public static MessageCreateParams BuildParameters(ChatRequest request)
    {
        var messages = new List<MessageParam>();
        foreach (var turn in request.Turns)
        {
            var content = new List<ContentBlockParam>();
            foreach (var part in turn.Parts)
            {
                switch (part)
                {
                    case TextPart text when text.Text.Length > 0:
                        content.Add(new TextBlockParam { Text = text.Text });
                        break;
                    // Thinking goes back exactly as it was issued; the signature must not be altered.
                    case ReasoningPart { RedactedData: { } data }:
                        content.Add(new RedactedThinkingBlockParam { Data = data });
                        break;
                    case ReasoningPart { Signature: { } signature } reasoning:
                        content.Add(new ThinkingBlockParam { Thinking = reasoning.Text, Signature = signature });
                        break;
                    case ToolCallPart call:
                        content.Add(new ToolUseBlockParam
                        {
                            ID = call.Id,
                            Name = call.Name,
                            Input = call.Input.ValueKind == JsonValueKind.Object
                                ? call.Input.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                                : new Dictionary<string, JsonElement>(),
                        });
                        break;
                    case ToolResultPart result:
                        content.Add(new ToolResultBlockParam
                        {
                            ToolUseID = result.ToolCallId,
                            Content = result.Content,
                            IsError = result.IsError,
                        });
                        break;
                }
            }
            if (content.Count == 0) continue;

            // Tool results travel in a user message; all results of a step in the same one.
            if (turn.Role == "assistant") messages.Add(new AssistantMessageParam { Content = content });
            else messages.Add(new UserMessageParam { Content = content });
        }

        var tools = request.Tools.Select(t => (ToolUnion)new Tool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = new()
            {
                Properties = t.InputSchema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
                    ? properties.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                    : new Dictionary<string, JsonElement>(),
                Required = t.InputSchema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
                    ? required.EnumerateArray().Select(r => r.GetString()!).ToList()
                    : [],
            },
            // Stream large tool inputs (file contents) as they are written; the caller validates the JSON.
            EagerInputStreaming = true,
        }).ToList();

        var parameters = new MessageCreateParams
        {
            Model = request.ModelId,
            MaxTokens = MaxTokens,
            Messages = messages,
            Tools = tools.Count > 0 ? tools : null,
        };
        if (!string.IsNullOrWhiteSpace(request.System))
        {
            parameters = parameters with { System = request.System };
        }
        // Adaptive thinking with a readable summary, on the models that take it.
        if (UsesAdaptiveThinking(request.ModelId))
        {
            parameters = parameters with { Thinking = new ThinkingConfigAdaptive { Display = Display.Summarized } };
        }
        return parameters;
    }

    /// <summary>
    /// Claude 4.6 and later take adaptive thinking; 4.5 and older do not (they are sent no thinking setting).
    /// Read from the version in the id — "claude-opus-5-5", "claude-haiku-4-5", "claude-3-5-sonnet-…" — so that
    /// any Claude model can be named, not only the listed ones. An id with no version in it is taken to be new.
    /// </summary>
    public static bool UsesAdaptiveThinking(string modelId)
    {
        var match = System.Text.RegularExpressions.Regex.Match(modelId, @"^claude-(?:[a-z]+-)?(\d+)(?:-(\d+))?");
        if (!match.Success) return true;
        var major = int.Parse(match.Groups[1].Value);
        // A long number after the major version is a release date ("claude-sonnet-4-20250514"), not a minor version.
        var minor = match.Groups[2].Success && match.Groups[2].Value.Length <= 2 ? int.Parse(match.Groups[2].Value) : 0;
        return major > 4 || (major == 4 && minor >= 6);
    }
}
