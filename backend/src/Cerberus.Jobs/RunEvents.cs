using System.Text;

namespace Cerberus.Jobs;

/// <summary>
/// The kinds of event on a run's stream (PLAN.md §6.5). A client renders a run
/// purely from these, in sequence order; it never has to poll for meaning.
/// </summary>
public static class RunEventTypes
{
    /// <summary>The job changed state. Data: the job, as <c>GET /jobs/{id}</c> returns it.</summary>
    public const string State = "state";

    /// <summary>The run's plan: every step it intends to take, in order. Data: <c>{ steps: [{ id, title }] }</c>.</summary>
    public const string Plan = "plan";

    /// <summary>A step of the run's task list started or ended. Data: <c>{ id, title, status }</c>.</summary>
    public const string Step = "step";

    /// <summary>Progress within the current step. Data: <c>{ done, total, message }</c>.</summary>
    public const string Progress = "progress";

    /// <summary>A finding, as soon as it is known. Data: the finding.</summary>
    public const string Finding = "finding";

    /// <summary>Text as a model produces it. Data: <c>{ channel, text }</c>; concatenate per channel.</summary>
    public const string Text = "text";

    /// <summary>A model's opinion on one finding. Data: the triage result.</summary>
    public const string Triage = "triage";

    /// <summary>An agent calls a tool. Data: <c>{ id, name, input }</c>.</summary>
    public const string ToolCall = "tool_call";

    /// <summary>The tool answered. Data: <c>{ id, name, output, truncated, isError }</c>; <c>id</c> matches the call.</summary>
    public const string ToolResult = "tool_result";

    /// <summary>A plain remark from the run. Data: <c>{ message }</c>.</summary>
    public const string Note = "note";

    /// <summary>The run is waiting for the operator. Data: the approval request.</summary>
    public const string ApprovalRequested = "approval_requested";

    /// <summary>The operator decided, or the request was withdrawn. Data: the approval request.</summary>
    public const string ApprovalResolved = "approval_resolved";

    /// <summary>The run asks the operator something and waits. Data: the question.</summary>
    public const string Question = "question";

    /// <summary>The question was answered, expired or withdrawn. Data: the question, with its answer.</summary>
    public const string QuestionResolved = "question_resolved";

    /// <summary>The run produced more events than are kept; only state changes follow. Data: <c>{ message }</c>.</summary>
    public const string Truncated = "truncated";
}

public static class StepStatus
{
    /// <summary>Planned, not started yet.</summary>
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Skipped = "skipped";
}

/// <summary>
/// What a piece of work uses to tell the operator what it is doing. Work that
/// runs outside a job (a synchronous request) gets <see cref="NullRunReporter"/>.
/// </summary>
public interface IRunReporter
{
    Task StepAsync(string id, string title, string status);

    Task ProgressAsync(int done, int total, string? message = null);

    Task FindingAsync(object finding);

    /// <summary>Streams model output. Deltas are batched; call <see cref="FlushTextAsync"/> when a reply ends.</summary>
    Task TextAsync(string channel, string delta);

    Task FlushTextAsync();

    Task EventAsync(string type, object data);

    /// <summary>
    /// Asks the operator and waits for the answer. Returns null when there is
    /// nobody to ask (work running outside a job) or nobody answered in time —
    /// the caller must then fall back to its safe default.
    /// </summary>
    Task<OperatorAnswer?> AskAsync(OperatorQuestionSpec question, CancellationToken ct);
}

public sealed class NullRunReporter : IRunReporter
{
    public static readonly NullRunReporter Instance = new();

    public Task StepAsync(string id, string title, string status) => Task.CompletedTask;

    public Task ProgressAsync(int done, int total, string? message = null) => Task.CompletedTask;

    public Task FindingAsync(object finding) => Task.CompletedTask;

    public Task TextAsync(string channel, string delta) => Task.CompletedTask;

    public Task FlushTextAsync() => Task.CompletedTask;

    public Task EventAsync(string type, object data) => Task.CompletedTask;

    public Task<OperatorAnswer?> AskAsync(OperatorQuestionSpec question, CancellationToken ct) =>
        Task.FromResult<OperatorAnswer?>(null);
}

/// <summary>
/// Batches a model's token stream into readable chunks: one event per token
/// would flood the log, one per reply would not be live. Emits when the buffer
/// is long enough or ends a line, and whenever the channel changes.
/// </summary>
public sealed class TextCoalescer(int flushAt = 160)
{
    private readonly StringBuilder _buffer = new();
    private string? _channel;

    /// <summary>Adds a delta; returns the chunks that are now ready to emit, in order.</summary>
    public IReadOnlyList<(string Channel, string Text)> Add(string channel, string delta)
    {
        var ready = new List<(string, string)>(2);
        if (_channel is not null && _channel != channel && Take() is { } previous) ready.Add(previous);

        _channel = channel;
        _buffer.Append(delta);
        if (_buffer.Length >= flushAt || (delta.EndsWith('\n') && _buffer.Length >= flushAt / 4))
        {
            if (Take() is { } chunk) ready.Add(chunk);
        }
        return ready;
    }

    /// <summary>Whatever is still buffered, or null.</summary>
    public (string Channel, string Text)? Flush() => Take();

    private (string Channel, string Text)? Take()
    {
        if (_channel is null || _buffer.Length == 0) return null;
        var chunk = (_channel, _buffer.ToString());
        _buffer.Clear();
        return chunk;
    }
}
