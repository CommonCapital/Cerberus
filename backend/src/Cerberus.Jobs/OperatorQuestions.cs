using System.Diagnostics;

namespace Cerberus.Jobs;

public sealed record QuestionOption(string Label, string? Description = null);

/// <summary>
/// A question a run puts to the operator (PLAN.md §6.5): text, optional
/// choices, and whether several choices or a free-text answer are accepted.
/// </summary>
public sealed record OperatorQuestionSpec(
    string Text,
    IReadOnlyList<QuestionOption> Options,
    bool MultiSelect = false,
    bool AllowText = false);

public sealed record OperatorAnswer(IReadOnlyList<string> Selected, string? Text);

public static class OperatorQuestions
{
    public const int MaxOptions = 8;
    public const int MaxTextLength = 2000;

    /// <summary>A question must be answerable: it has choices, or accepts free text.</summary>
    public static string? ValidateSpec(OperatorQuestionSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Text)) return "A question needs text.";
        if (spec.Options.Count == 0 && !spec.AllowText) return "A question needs options or must allow a free-text answer.";
        if (spec.Options.Count > MaxOptions) return $"A question can have at most {MaxOptions} options.";
        if (spec.Options.Any(o => string.IsNullOrWhiteSpace(o.Label))) return "Every option needs a label.";
        if (spec.Options.Select(o => o.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count() != spec.Options.Count)
        {
            return "Option labels must be unique.";
        }
        return null;
    }

    /// <summary>Checks an operator's reply against the question and normalizes it.</summary>
    public static (OperatorAnswer? Answer, string? Error) ValidateAnswer(
        OperatorQuestionSpec spec, IReadOnlyList<string>? selected, string? text)
    {
        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (text is not null && !spec.AllowText) return (null, "This question does not take a free-text answer; pick one of its options.");
        if (text?.Length > MaxTextLength) return (null, $"The answer text is limited to {MaxTextLength} characters.");

        // Match labels case-insensitively and return them in the question's own wording and order.
        var wanted = (selected ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        var unknown = wanted.FirstOrDefault(w => !spec.Options.Any(o => o.Label.Equals(w, StringComparison.OrdinalIgnoreCase)));
        if (unknown is not null)
        {
            return (null, $"\"{unknown}\" is not one of the options: {string.Join(", ", spec.Options.Select(o => o.Label))}.");
        }
        var chosen = spec.Options.Select(o => o.Label)
            .Where(label => wanted.Contains(label, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (chosen.Count == 0 && text is null)
        {
            return (null, spec.Options.Count == 0 ? "An answer text is required." : "Select an option" + (spec.AllowText ? " or give an answer text." : "."));
        }
        if (chosen.Count > 1 && !spec.MultiSelect) return (null, "Select exactly one option.");

        return (new OperatorAnswer(chosen, text), null);
    }
}

/// <summary>
/// A job's time limit, which can be paused while the job waits for a person:
/// time spent waiting for the operator is not the job's fault and must not
/// time it out. Pauses nest; the limit resumes with the time it had left.
/// </summary>
public sealed class JobTimeLimit
{
    private readonly CancellationTokenSource _source;
    private readonly TimeSpan _timeout;
    private readonly Stopwatch _running = new();
    private readonly object _lock = new();
    private int _suspensions;

    internal JobTimeLimit(CancellationTokenSource source, TimeSpan timeout)
    {
        _source = source;
        _timeout = timeout;
    }

    /// <summary>A limit that never fires and ignores pauses, for work that runs outside a job.</summary>
    public static JobTimeLimit None { get; } = new(new CancellationTokenSource(), Timeout.InfiniteTimeSpan);

    internal void Start()
    {
        lock (_lock)
        {
            _running.Start();
            Arm();
        }
    }

    /// <summary>Stops the clock until the returned handle is disposed.</summary>
    public IDisposable Suspend()
    {
        lock (_lock)
        {
            if (_suspensions++ == 0)
            {
                _running.Stop();
                TryCancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
        return new Resumer(this);
    }

    private void Resume()
    {
        lock (_lock)
        {
            if (--_suspensions == 0)
            {
                _running.Start();
                Arm();
            }
        }
    }

    private void Arm()
    {
        if (_timeout == Timeout.InfiniteTimeSpan) return;
        var left = _timeout - _running.Elapsed;
        TryCancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
    }

    private void TryCancelAfter(TimeSpan delay)
    {
        try
        {
            _source.CancelAfter(delay);
        }
        catch (ObjectDisposedException)
        {
            // The job has already ended; nothing left to time.
        }
    }

    private sealed class Resumer(JobTimeLimit owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Resume();
        }
    }
}
