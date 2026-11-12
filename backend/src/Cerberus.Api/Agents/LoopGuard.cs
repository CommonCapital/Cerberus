using System.Text;
using System.Text.Json;

namespace Cerberus.Api.Agents;

/// <summary>
/// Limits on an agent run that are enforced in code rather than asked for in
/// the prompt (PLAN.md §21). A model can loop: ask for the same thing again
/// and again, or keep making calls that fail. The step limit ends that
/// eventually; this ends it as soon as it is visible, and says why:
/// <list type="bullet">
/// <item>the same call — same tool, same arguments — runs at most
/// <see cref="MaxIdenticalCalls"/> times; after that it is answered with a
/// note instead of being run, since its result would be the same;</item>
/// <item>a run has a budget of tool calls; when it is spent, no more are made;</item>
/// <item>after <see cref="MaxFruitlessSteps"/> steps in a row in which no call
/// did anything (all repeated or failed), the run is stopped.</item>
/// </list>
/// A stopped run still ends with the model's own account: the loop gives it
/// one last turn without tools.
/// </summary>
public sealed class LoopGuard(int maxToolCalls)
{
    public const int MaxIdenticalCalls = 2;
    public const int MaxFruitlessSteps = 3;

    /// <summary>The tool-call budget for a run of this many steps: room for several calls per step, never less than 20.</summary>
    public static int BudgetFor(int maxSteps) => Math.Max(20, maxSteps * 5);

    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);
    private int _calls;
    private int _fruitless;

    /// <summary>Set once the run must end: why, as the rest of the sentence "The agent was stopped: it …".</summary>
    public string? StopReason { get; private set; }

    /// <summary>
    /// Asked before a call runs. Null means run it; otherwise the call is not
    /// made and the text is what the model gets as its (failed) result.
    /// </summary>
    public string? Refuse(string tool, JsonElement input)
    {
        _calls++;
        if (_calls > maxToolCalls)
        {
            StopReason ??= $"it used up its budget of {maxToolCalls} tool calls";
            return "This run's budget of tool calls is used up; the call was not made.";
        }

        var key = tool + "\n" + Canonical(input);
        var count = _seen[key] = _seen.GetValueOrDefault(key) + 1;
        return count > MaxIdenticalCalls
            ? $"You have already made this exact call {MaxIdenticalCalls} times; it was not made again, because its result would be the same. " +
              "Work with the result you have, or do something different."
            : null;
    }

    /// <summary>Told after each step whether any of its calls did something: ran, and did not fail.</summary>
    public void StepDone(bool useful)
    {
        _fruitless = useful ? 0 : _fruitless + 1;
        if (_fruitless >= MaxFruitlessSteps)
        {
            StopReason ??= $"its last {MaxFruitlessSteps} steps got nowhere (tool calls that were repeated or failed)";
        }
    }

    /// <summary>What the model is told when it is stopped for a reason other than the step limit.</summary>
    public static string ClosingTurn(string reason) =>
        $"You have been stopped: {reason}. You cannot call any more tools. Reply now with a short summary: " +
        "what you did, what you found or recorded, and what you did not get to.";

    /// <summary>
    /// The arguments in one fixed form, so the same call is recognised however
    /// it was written: object keys sorted, no insignificant whitespace.
    /// </summary>
    public static string Canonical(JsonElement value)
    {
        var text = new StringBuilder();
        Write(value, text);
        return text.ToString();
    }

    private static void Write(JsonElement value, StringBuilder text)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                text.Append('{');
                var first = true;
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) text.Append(',');
                    first = false;
                    text.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    Write(property.Value, text);
                }
                text.Append('}');
                break;
            case JsonValueKind.Array:
                text.Append('[');
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (index++ > 0) text.Append(',');
                    Write(item, text);
                }
                text.Append(']');
                break;
            default:
                text.Append(value.GetRawText());
                break;
        }
    }
}
