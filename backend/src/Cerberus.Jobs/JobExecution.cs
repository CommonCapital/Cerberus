using System.Collections.Concurrent;

namespace Cerberus.Jobs;

public enum JobState
{
    /// <summary>Accepted, but waiting for the operator to approve it. Nothing runs until they do.</summary>
    AwaitingApproval,
    Queued,
    Running,
    Succeeded,
    Failed,

    /// <summary>Stopped by the operator (cancel, or an engagement abort).</summary>
    Cancelled,

    /// <summary>Stopped by the platform because it exceeded its time limit.</summary>
    TimedOut,
}

public static class JobStates
{
    public static bool IsTerminal(JobState state) =>
        state is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.TimedOut;

    /// <summary>
    /// Jobs only move forward: (AwaitingApproval →) Queued → Running → a terminal
    /// state. A job that has not started can be cancelled or failed directly.
    /// </summary>
    public static bool CanTransition(JobState from, JobState to) => from switch
    {
        JobState.AwaitingApproval => to is JobState.Queued or JobState.Cancelled or JobState.Failed,
        JobState.Queued => to is JobState.Running or JobState.Cancelled or JobState.Failed,
        JobState.Running => IsTerminal(to),
        _ => false,
    };
}

/// <summary>How a job ended. <see cref="Abandoned"/> means the work ignored the stop signal.</summary>
public sealed record JobOutcome(JobState State, string? ResultId, string? Error, bool Abandoned = false);

/// <summary>
/// Tracks the cancel handle of every running job so the kill switch can reach
/// it from any request. Process-local, like the in-process bus.
/// </summary>
public sealed class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new();

    internal CancellationTokenSource Register(string jobId)
    {
        var cts = new CancellationTokenSource();
        _running[jobId] = cts;
        return cts;
    }

    internal void Unregister(string jobId)
    {
        if (_running.TryRemove(jobId, out var cts)) cts.Dispose();
    }

    /// <summary>Signals a running job to stop. False if the job is not running in this process.</summary>
    public bool Cancel(string jobId)
    {
        if (!_running.TryGetValue(jobId, out var cts)) return false;
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;   // finished between lookup and cancel
        }
    }

    public bool IsRunning(string jobId) => _running.ContainsKey(jobId);
}

/// <summary>
/// Runs one job's work under the platform's controls, independent of what the
/// work itself decides: an operator cancel, a time limit, and a hard stop —
/// if the work has not returned within the grace period after being told to
/// stop, it is abandoned and the job is closed anyway.
/// </summary>
public static class JobExecutor
{
    public static async Task<JobOutcome> RunAsync(
        string jobId,
        Func<CancellationToken, Task<string?>> work,
        JobCancellationRegistry registry,
        TimeSpan timeout,
        TimeSpan grace,
        CancellationToken shutdown)
    {
        var operatorCancel = registry.Register(jobId);
        using var timeLimit = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(operatorCancel.Token, timeLimit.Token, shutdown);
        using var hardStop = new CancellationTokenSource();
        // Once told to stop, the work has `grace` to comply before we stop waiting for it.
        using var arm = stop.Token.Register(() => hardStop.CancelAfter(grace));

        try
        {
            timeLimit.CancelAfter(timeout);
            var task = Task.Run(() => work(stop.Token), CancellationToken.None);
            try
            {
                var resultId = await task.WaitAsync(hardStop.Token);
                return new JobOutcome(JobState.Succeeded, resultId, null);
            }
            catch (OperationCanceledException)
            {
                var abandoned = !task.IsCompleted;
                if (abandoned)
                {
                    // Observe the eventual result so a late failure is not an unobserved exception.
                    _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                }

                // Why it stopped, in order of precedence.
                if (operatorCancel.IsCancellationRequested)
                {
                    return new JobOutcome(JobState.Cancelled, null, "Cancelled by the operator.", abandoned);
                }
                if (timeLimit.IsCancellationRequested)
                {
                    return new JobOutcome(JobState.TimedOut, null, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Exceeded the time limit of {timeout.TotalSeconds:0.###} s."), abandoned);
                }
                return new JobOutcome(JobState.Failed, null, "Interrupted by a server shutdown.", abandoned);
            }
            catch (Exception ex)
            {
                return new JobOutcome(JobState.Failed, null, ex.Message);
            }
        }
        finally
        {
            registry.Unregister(jobId);
        }
    }
}
