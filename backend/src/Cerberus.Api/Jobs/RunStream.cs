using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Jobs;

/// <summary>
/// The event stream of every run (PLAN.md §6.5). Each event gets the next
/// sequence number of its job, is stored, and is then pushed to the owner over
/// SignalR as <c>jobEvent</c>. Storing first means a client that was offline —
/// or connects later — replays the run from <c>GET /jobs/{id}/events</c> and
/// continues live from the last sequence it saw.
/// </summary>
public sealed class RunStream(IHubContext<JobsHub> hub, IServiceScopeFactory scopes, ILogger<RunStream> logger)
{
    /// <summary>Events kept per job. Beyond it only state changes are recorded.</summary>
    public const int MaxEventsPerJob = 5000;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    // One writer at a time keeps each job's sequence gap-free and the push order
    // equal to the stored order. The unique (JobId, Sequence) index is the backstop.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _lastSequence = new();

    public async Task EmitAsync(string jobId, string userId, string type, object data)
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();

                if (!_lastSequence.TryGetValue(jobId, out var last))
                {
                    last = await db.JobEvents.Where(e => e.JobId == jobId).MaxAsync(e => (long?)e.Sequence) ?? 0;
                }

                // A runaway run must not fill the database: past the cap, say so once and keep only state changes.
                if (last >= MaxEventsPerJob && type != RunEventTypes.State)
                {
                    if (last > MaxEventsPerJob) return;
                    type = RunEventTypes.Truncated;
                    data = new { message = $"This run produced more than {MaxEventsPerJob} events; only state changes are recorded from here on." };
                }

                var record = new JobEvent
                {
                    JobId = jobId,
                    UserId = userId,
                    Sequence = last + 1,
                    Type = type,
                    Data = JsonSerializer.Serialize(data, JsonOptions),
                    CreatedAt = DateTime.UtcNow,
                };
                db.JobEvents.Add(record);
                await db.SaveChangesAsync();
                _lastSequence[jobId] = record.Sequence;

                await hub.Clients.Group(JobsHub.GroupFor(userId)).SendAsync(JobsHub.JobEvent, Describe(record));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            // Telling the operator is best-effort; it must never break the work itself.
            logger.LogWarning(ex, "Could not record a {Type} event for job {JobId}.", type, jobId);
        }
    }

    /// <summary>Stops tracking a finished job's sequence (it is re-read from the database if ever needed).</summary>
    public void Forget(string jobId) => _lastSequence.TryRemove(jobId, out _);

    /// <param name="ask">How this run reaches the operator; null if it cannot ask.</param>
    public IRunReporter For(Job job, Func<OperatorQuestionSpec, CancellationToken, Task<OperatorAnswer?>>? ask = null) =>
        new Reporter(this, job.Id, job.UserId, ask);

    internal static object Describe(JobEvent e) => new
    {
        e.JobId,
        e.Sequence,
        e.Type,
        data = JsonSerializer.Deserialize<JsonElement>(e.Data),
        e.CreatedAt,
    };

    private sealed class Reporter(
        RunStream stream, string jobId, string userId,
        Func<OperatorQuestionSpec, CancellationToken, Task<OperatorAnswer?>>? ask) : IRunReporter
    {
        /// <summary>Findings pushed one by one per run; the rest are summarized (all are in the stored result).</summary>
        private const int MaxFindingEvents = 300;

        private readonly TextCoalescer _text = new();
        private int _findings;

        public Task StepAsync(string id, string title, string status) =>
            EventAsync(RunEventTypes.Step, new { id, title, status });

        public Task ProgressAsync(int done, int total, string? message = null) =>
            EventAsync(RunEventTypes.Progress, new { done, total, message });

        public async Task FindingAsync(object finding)
        {
            var n = Interlocked.Increment(ref _findings);
            if (n <= MaxFindingEvents)
            {
                await EventAsync(RunEventTypes.Finding, finding);
            }
            else if (n == MaxFindingEvents + 1)
            {
                await EventAsync(RunEventTypes.Note, new
                {
                    message = $"More than {MaxFindingEvents} findings; the rest are not streamed one by one. The full list is in the result.",
                });
            }
        }

        /// <summary>
        /// A model may quote the code it read. What is kept of its text is masked per chunk — best effort:
        /// a credential that happens to be split across two chunks is not recognised.
        /// </summary>
        private static string Mask(string text) => Cerberus.Scanning.SecretMasker.MaskText(text);

        public async Task TextAsync(string channel, string delta)
        {
            foreach (var (c, text) in _text.Add(channel, delta))
            {
                await stream.EmitAsync(jobId, userId, RunEventTypes.Text, new { channel = c, text = Mask(text) });
            }
        }

        public async Task FlushTextAsync()
        {
            if (_text.Flush() is { } chunk)
            {
                await stream.EmitAsync(jobId, userId, RunEventTypes.Text, new { channel = chunk.Channel, text = Mask(chunk.Text) });
            }
        }

        public async Task<OperatorAnswer?> AskAsync(OperatorQuestionSpec question, CancellationToken ct)
        {
            await FlushTextAsync();
            return ask is null ? null : await ask(question, ct);
        }

        public async Task EventAsync(string type, object data)
        {
            await FlushTextAsync();   // keep text in order with whatever follows it
            await stream.EmitAsync(jobId, userId, type, data);
        }
    }
}
