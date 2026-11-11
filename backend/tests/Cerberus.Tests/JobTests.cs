using Cerberus.Jobs;
using Cerberus.Scanning;

namespace Cerberus.Tests;

public class JobExecutorTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(200);

    private static Task<JobOutcome> Run(
        JobCancellationRegistry registry, Func<CancellationToken, Task<string?>> work,
        TimeSpan? timeout = null, CancellationToken shutdown = default) =>
        JobExecutor.RunAsync("job-1", work, registry, timeout ?? Long, Grace, shutdown);

    /// <summary>Work that runs until told to stop, signalling once it has started.</summary>
    private static Func<CancellationToken, Task<string?>> Cooperative(TaskCompletionSource started) => async ct =>
    {
        started.TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
        return null;
    };

    [Fact]
    public async Task Work_that_completes_succeeds_with_its_result()
    {
        var outcome = await Run(new JobCancellationRegistry(), _ => Task.FromResult<string?>("scan-42"));

        Assert.Equal(new JobOutcome(JobState.Succeeded, "scan-42", null), outcome);
    }

    [Fact]
    public async Task Work_that_throws_fails_with_the_message()
    {
        var outcome = await Run(new JobCancellationRegistry(), _ => throw new InvalidOperationException("boom"));

        Assert.Equal(JobState.Failed, outcome.State);
        Assert.Equal("boom", outcome.Error);
    }

    [Fact]
    public async Task Operator_cancel_stops_running_work()
    {
        var registry = new JobCancellationRegistry();
        var started = new TaskCompletionSource();
        var run = Run(registry, Cooperative(started));
        await started.Task;

        Assert.True(registry.IsRunning("job-1"));
        Assert.True(registry.Cancel("job-1"));
        var outcome = await run;

        Assert.Equal(JobState.Cancelled, outcome.State);
        Assert.False(outcome.Abandoned);
        Assert.False(registry.IsRunning("job-1"));
    }

    [Fact]
    public async Task Work_that_exceeds_the_time_limit_times_out()
    {
        var outcome = await Run(new JobCancellationRegistry(), Cooperative(new TaskCompletionSource()),
            timeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(JobState.TimedOut, outcome.State);
        Assert.False(outcome.Abandoned);
    }

    [Fact]
    public async Task Work_that_ignores_the_stop_signal_is_abandoned_after_the_grace_period()
    {
        var release = new TaskCompletionSource<string?>();
        var started = new TaskCompletionSource();
        var registry = new JobCancellationRegistry();
        var run = Run(registry, _ =>
        {
            started.TrySetResult();
            return release.Task;          // never looks at its token
        });
        await started.Task;

        registry.Cancel("job-1");
        var outcome = await run;          // returns despite the work still being stuck
        release.TrySetResult(null);

        Assert.Equal(JobState.Cancelled, outcome.State);
        Assert.True(outcome.Abandoned);
    }

    [Fact]
    public async Task Operator_cancel_takes_precedence_over_a_simultaneous_timeout()
    {
        var registry = new JobCancellationRegistry();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource<string?>();
        var run = Run(registry, async ct =>
        {
            started.TrySetResult();
            await release.Task;           // hold until both signals have fired
            ct.ThrowIfCancellationRequested();
            return null;
        }, timeout: TimeSpan.FromMilliseconds(30));
        await started.Task;
        registry.Cancel("job-1");
        await Task.Delay(60);
        release.TrySetResult(null);

        Assert.Equal(JobState.Cancelled, (await run).State);
    }

    [Fact]
    public async Task Server_shutdown_fails_the_job()
    {
        using var shutdown = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var run = Run(new JobCancellationRegistry(), Cooperative(started), shutdown: shutdown.Token);
        await started.Task;

        shutdown.Cancel();
        var outcome = await run;

        Assert.Equal(JobState.Failed, outcome.State);
        Assert.Contains("shutdown", outcome.Error);
    }

    [Fact]
    public void Cancelling_a_job_that_is_not_running_reports_false()
    {
        Assert.False(new JobCancellationRegistry().Cancel("nope"));
    }
}

public class JobStateTests
{
    [Theory]
    [InlineData(JobState.AwaitingApproval, JobState.Queued, true)]      // approved
    [InlineData(JobState.AwaitingApproval, JobState.Cancelled, true)]   // denied or withdrawn
    [InlineData(JobState.AwaitingApproval, JobState.Running, false)]    // never straight to running
    [InlineData(JobState.AwaitingApproval, JobState.Succeeded, false)]
    [InlineData(JobState.Queued, JobState.AwaitingApproval, false)]
    [InlineData(JobState.Running, JobState.AwaitingInput, true)]        // asks the operator
    [InlineData(JobState.AwaitingInput, JobState.Running, true)]        // answered
    [InlineData(JobState.AwaitingInput, JobState.Cancelled, true)]      // stopped while waiting
    [InlineData(JobState.Queued, JobState.AwaitingInput, false)]        // only a running job can ask
    [InlineData(JobState.Queued, JobState.Running, true)]
    [InlineData(JobState.Queued, JobState.Cancelled, true)]
    [InlineData(JobState.Queued, JobState.Succeeded, false)]
    [InlineData(JobState.Running, JobState.Succeeded, true)]
    [InlineData(JobState.Running, JobState.TimedOut, true)]
    [InlineData(JobState.Running, JobState.Queued, false)]
    [InlineData(JobState.Succeeded, JobState.Running, false)]
    [InlineData(JobState.Cancelled, JobState.Queued, false)]
    public void Jobs_only_move_forward(JobState from, JobState to, bool allowed)
    {
        Assert.Equal(allowed, JobStates.CanTransition(from, to));
    }
}

public class InProcMessageBusTests
{
    [Fact]
    public async Task Each_message_is_delivered_once_in_order()
    {
        var bus = new InProcMessageBus();
        foreach (var id in new[] { "a", "b", "c" }) await bus.PublishAsync(new JobMessage(id));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<string>();
        await foreach (var message in bus.ConsumeAsync(cts.Token))
        {
            received.Add(message.JobId);
            if (received.Count == 3) break;
        }

        Assert.Equal(["a", "b", "c"], received);
    }

    [Fact]
    public async Task Competing_consumers_share_the_messages_without_duplicates()
    {
        var bus = new InProcMessageBus();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new System.Collections.Concurrent.ConcurrentBag<string>();
        var remaining = 100;
        var done = new TaskCompletionSource();

        async Task Consume()
        {
            try
            {
                await foreach (var message in bus.ConsumeAsync(cts.Token))
                {
                    received.Add(message.JobId);
                    if (Interlocked.Decrement(ref remaining) == 0) done.TrySetResult();
                }
            }
            catch (OperationCanceledException) { }
        }

        var consumers = new[] { Consume(), Consume(), Consume() };
        for (var i = 0; i < 100; i++) await bus.PublishAsync(new JobMessage(i.ToString()));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Task.WhenAll(consumers);

        Assert.Equal(100, received.Count);
        Assert.Equal(100, received.Distinct().Count());
    }
}

public class ScanCancellationTests
{
    [Fact]
    public void A_cancelled_scan_stops_instead_of_returning_partial_results()
    {
        var root = Path.Combine(Path.GetTempPath(), "cerberus-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.py"), "import hashlib\nhashlib.md5(b'x')\n");
            var scanner = new ScanService(new ScanOptions(root));

            Assert.Equal(1, scanner.Scan(".").FilesScanned);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => scanner.Scan(".", cts.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
