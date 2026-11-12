using System.Text.Json;
using Cerberus.Api.Audit;
using Cerberus.Api.Auth;
using Cerberus.Data;
using Cerberus.Jobs;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Jobs;

/// <param name="Every">How often: a number of minutes, or "90m", "6h", "1d", "1w".</param>
/// <param name="FirstRunAt">When the first run is due (UTC); default: one interval from now.</param>
/// <param name="Job">The job to start each time — exactly what <c>POST /jobs</c> takes.</param>
public sealed record CreateScheduleRequest(string? Name, JsonElement? Every, DateTime? FirstRunAt, SubmitJobRequest? Job);

public sealed record UpdateScheduleRequest(string? Name, JsonElement? Every, bool? Enabled);

/// <summary>What became of one firing of a schedule.</summary>
/// <param name="Kind">started, waiting, skipped or refused.</param>
public sealed record ScheduleFiring(string Kind, string Detail, string? JobId);

/// <summary>
/// Recurring runs (PLAN.md §22): a schedule is a stored job request and an
/// interval. When it is due, the request is submitted <i>through the same
/// path as a request from the operator</i> — the same validation, the same
/// engagement scope, the same permission policy. A scheduled run that needs
/// approval therefore waits for it like any other, and "always allow" works
/// for it like for any other: nothing about consent is decided here.
/// </summary>
public sealed class ScheduleRunner(IServiceScopeFactory scopes, AuditLog audit, ILogger<ScheduleRunner> logger)
{
    public const int MinIntervalMinutes = 15;
    public const int MaxIntervalMinutes = 60 * 24 * 30;
    public const int MaxPerUser = 20;

    /// <summary>After this many firings in a row that the server refused, a schedule is paused: it would only keep failing.</summary>
    public const int MaxRefusals = 3;

    /// <summary>Jobs that work on something that exists once (a scan's findings, one assessment) are not for repeating.</summary>
    public static readonly IReadOnlySet<string> Schedulable =
        new HashSet<string> { JobTypes.Scan, JobTypes.Review, JobTypes.Sast, JobTypes.ThreatModel, JobTypes.Index, JobTypes.Assess };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>"90m", "6h", "1d", "1w" or a plain number of minutes. Null when it is none of these or out of range.</summary>
    public static int? ParseInterval(JsonElement? every)
    {
        if (every is not { } value) return null;
        int? minutes = null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) minutes = number;
        else if (value.ValueKind == JsonValueKind.String && value.GetString()?.Trim().ToLowerInvariant() is { Length: > 0 } text)
        {
            var unit = text[^1] switch { 'm' => 1, 'h' => 60, 'd' => 60 * 24, 'w' => 60 * 24 * 7, _ => 0 };
            var digits = unit == 0 ? text : text[..^1];
            if (int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) && count <= 100_000)
            {
                minutes = count * (unit == 0 ? 1 : unit);
            }
        }
        return minutes is >= MinIntervalMinutes and <= MaxIntervalMinutes ? minutes : null;
    }

    public static string DescribeInterval(int minutes) =>
        minutes % (60 * 24 * 7) == 0 ? Count(minutes / (60 * 24 * 7), "week")
        : minutes % (60 * 24) == 0 ? Count(minutes / (60 * 24), "day")
        : minutes % 60 == 0 ? Count(minutes / 60, "hour")
        : Count(minutes, "minute");

    private static string Count(int n, string unit) => n == 1 ? $"every {unit}" : $"every {n} {unit}s";

    /// <summary>
    /// The next time after <paramref name="now"/> on the schedule's own grid. After downtime a schedule
    /// fires once and carries on from there; it does not make up for every run it missed.
    /// </summary>
    public static DateTime NextAfter(DateTime due, int intervalMinutes, DateTime now)
    {
        if (due > now) return due;
        var missed = (long)Math.Floor((now - due).TotalMinutes / intervalMinutes) + 1;
        return due.AddMinutes(missed * intervalMinutes);
    }

    /// <summary>Fires every schedule that is due. Safe to call from several instances at once: each due run is claimed by one.</summary>
    public async Task<int> RunDueAsync(DateTime now, CancellationToken ct)
    {
        List<(string Id, DateTime Due, int Interval)> due;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
            due = (await db.Schedules.AsNoTracking()
                    .Where(s => s.Enabled && s.NextRunAt <= now)
                    .OrderBy(s => s.NextRunAt)
                    .Select(s => new { s.Id, s.NextRunAt, s.IntervalMinutes })
                    .ToListAsync(ct))
                .Select(s => (s.Id, s.NextRunAt, s.IntervalMinutes)).ToList();
        }

        var fired = 0;
        foreach (var (id, dueAt, interval) in due)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await FireAsync(id, dueAt, NextAfter(dueAt, interval, now), now, ct) is not null) fired++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One schedule that cannot be fired must not stop the others.
                logger.LogError(ex, "Schedule {ScheduleId} could not be fired.", id);
            }
        }
        return fired;
    }

    /// <summary>Fires one schedule now, whatever its next time is ("run now"). Its own rhythm is left as it was.</summary>
    public Task<ScheduleFiring?> RunNowAsync(string id, DateTime now, CancellationToken ct) => FireAsync(id, null, null, now, ct);

    private async Task<ScheduleFiring?> FireAsync(string id, DateTime? dueAt, DateTime? next, DateTime now, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<CerberusDbContext>();

        if (dueAt is not null)
        {
            // The claim: only the instance that moves the schedule on from this due time fires it.
            var claimed = await db.Schedules
                .Where(s => s.Id == id && s.Enabled && s.NextRunAt == dueAt)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextRunAt, next!.Value), ct);
            if (claimed == 0) return null;
        }

        var schedule = await db.Schedules.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null) return null;

        ScheduleFiring firing;
        var previous = schedule.LastJobId is null ? null
            : await db.Jobs.AsNoTracking().Where(j => j.Id == schedule.LastJobId).Select(j => j.State).FirstOrDefaultAsync(ct);
        if (previous is nameof(JobState.AwaitingApproval) or nameof(JobState.Queued) or nameof(JobState.Running) or nameof(JobState.AwaitingInput))
        {
            // Runs do not pile up behind one that is still going, or still waiting for the operator's answer.
            var why = previous == nameof(JobState.AwaitingApproval) ? "is still waiting for approval"
                : previous == nameof(JobState.AwaitingInput) ? "is still waiting for an answer" : "has not finished";
            firing = new ScheduleFiring("skipped", $"Skipped: the previous run {why}.", schedule.LastJobId);
        }
        else
        {
            firing = await SubmitAsync(schedule, services, ct);
            if (firing.JobId is not null) schedule.LastJobId = firing.JobId;
            schedule.Refusals = firing.Kind == "refused" ? schedule.Refusals + 1 : 0;
            if (schedule.Refusals >= MaxRefusals)
            {
                schedule.Enabled = false;
                firing = firing with { Detail = $"Paused after {MaxRefusals} refusals in a row. Last: {firing.Detail}" };
            }
        }

        schedule.LastRunAt = now;
        schedule.LastOutcome = firing.Detail.Length <= 500 ? firing.Detail : firing.Detail[..500];
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(schedule.UserId, "schedule.run", firing.Kind == "refused" ? AuditOutcome.Error : AuditOutcome.Ok,
            $"Schedule \"{schedule.Name}\" ({schedule.Id}): {firing.Detail}", schedule.EngagementId, schedule.TargetPath, ct: ct);
        return firing;
    }

    /// <summary>Submits the stored request as its owner would — and reads the answer the owner would have got.</summary>
    private static async Task<ScheduleFiring> SubmitAsync(Schedule schedule, IServiceProvider services, CancellationToken ct)
    {
        SubmitJobRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<SubmitJobRequest>(schedule.Request, Json);
        }
        catch (JsonException)
        {
            request = null;
        }
        if (request is null) return new ScheduleFiring("refused", "Refused: the stored job request cannot be read.", null);

        Job? job = null;
        ApprovalRequest? approval = null;
        var result = await JobEndpoints.SubmitAsync(
            schedule.UserId, request, services.GetRequiredService<CerberusDbContext>(), services.GetRequiredService<AuditLog>(),
            services.GetRequiredService<IMessageBus>(), services.GetRequiredService<JobOptions>(),
            services.GetRequiredService<Compliance.CatalogLoader>(), services.GetRequiredService<JobEvents>(),
            services.GetRequiredService<RunPolicy>(), services.GetRequiredService<Web.WebOptions>(), services.GetRequiredService<Web.IMcpServers>(), ct,
            accepted: (j, a) => (job, approval) = (j, a));

        if (job is null)
        {
            var error = "the server refused it";
            if (result is IValueHttpResult { Value: { } body })
            {
                using var json = JsonSerializer.SerializeToDocument(body, Json);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("error", out var said) && said.ValueKind == JsonValueKind.String)
                {
                    error = said.GetString()!;
                }
            }
            return new ScheduleFiring("refused", $"Refused: {error}", null);
        }
        return approval is null
            ? new ScheduleFiring("started", "Started.", job.Id)
            : new ScheduleFiring("waiting", $"Waiting for your approval: {approval.Title}.", job.Id);
    }
}

/// <summary>Looks for due schedules twice a minute.</summary>
public sealed class SchedulerService(ScheduleRunner runner, TimeProvider clock, ILogger<SchedulerService> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, clock);
        do
        {
            try
            {
                await runner.RunDueAsync(clock.GetUtcNow().UtcDateTime, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Looking for due schedules failed; trying again at the next tick.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public static class ScheduleEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapScheduleEndpoints(this WebApplication app)
    {
        var schedules = app.MapGroup("/schedules").AddEndpointFilter<RequireAuthFilter>();
        schedules.MapGet("/", List);
        schedules.MapPost("/", Create);
        schedules.MapGet("/{id}", Get);
        schedules.MapPut("/{id}", Update);
        schedules.MapDelete("/{id}", Delete);
        schedules.MapPost("/{id}/run", RunNow);
    }

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static IResult Bad(string error) => Results.Json(new { error }, statusCode: 400);

    private static IResult NotFound() => Results.Json(new { error = "Schedule not found" }, statusCode: 404);

    internal static object Describe(Schedule s) => new
    {
        s.Id, s.Name, type = s.JobType, s.TargetPath, s.EngagementId,
        everyMinutes = s.IntervalMinutes, every = ScheduleRunner.DescribeInterval(s.IntervalMinutes),
        s.Enabled, nextRunAt = StoredTime.Utc(s.NextRunAt),
        lastRunAt = s.LastRunAt is { } last ? StoredTime.Utc(last) : (DateTime?)null,
        s.LastOutcome, s.LastJobId, createdAt = StoredTime.Utc(s.CreatedAt),
    };

    private static async Task<IResult> List(HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var all = await db.Schedules.AsNoTracking().Where(s => s.UserId == userId).OrderBy(s => s.CreatedAt).ToListAsync(ct);
        return Results.Json(all.Select(Describe));
    }

    private static async Task<IResult> Get(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var schedule = await db.Schedules.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        return schedule is null ? NotFound() : Results.Json(Describe(schedule));
    }

    private static async Task<IResult> Create(
        CreateScheduleRequest body, HttpContext http, CerberusDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var userId = UserId(http);
        if (body.Job is null) return Bad("job is required: what to start each time, as for POST /jobs.");
        var type = string.IsNullOrWhiteSpace(body.Job.Type) ? JobTypes.Scan : body.Job.Type.Trim().ToLowerInvariant();
        if (!ScheduleRunner.Schedulable.Contains(type))
        {
            return Bad($"A {type} job cannot be scheduled. Schedulable: {string.Join(", ", ScheduleRunner.Schedulable.Order())}.");
        }
        if (type == JobTypes.Assess && !string.IsNullOrWhiteSpace(body.Job.ScanId))
        {
            return Bad("A scheduled assessment scans afresh each time: give a path, not a scanId.");
        }
        if (ScheduleRunner.ParseInterval(body.Every) is not { } minutes)
        {
            return Bad($"every must be a number of minutes or like \"6h\", \"1d\", \"1w\": between {ScheduleRunner.MinIntervalMinutes} minutes and 30 days.");
        }
        var now = clock.GetUtcNow().UtcDateTime;
        var first = body.FirstRunAt is { } asked ? StoredTime.Utc(asked) : now.AddMinutes(minutes);
        if (first < now.AddMinutes(-1)) return Bad("firstRunAt is in the past.");
        if (first > now.AddDays(366)) return Bad("firstRunAt is more than a year away.");
        if (await db.Schedules.CountAsync(s => s.UserId == userId, ct) >= ScheduleRunner.MaxPerUser)
        {
            return Bad($"You have {ScheduleRunner.MaxPerUser} schedules already. Remove one first.");
        }

        var path = string.IsNullOrWhiteSpace(body.Job.Path) ? "." : body.Job.Path!.Trim();
        var name = Cerberus.Api.Chat.SessionTitler.CleanManual(body.Name) ?? $"{type} of {path}";
        var schedule = new Schedule
        {
            UserId = userId,
            Name = name,
            JobType = type,
            TargetPath = path,
            EngagementId = string.IsNullOrWhiteSpace(body.Job.EngagementId) ? null : body.Job.EngagementId,
            Request = JsonSerializer.Serialize(body.Job with { Type = type }, Json),
            IntervalMinutes = minutes,
            NextRunAt = first,
            CreatedAt = now,
        };
        db.Schedules.Add(schedule);
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(userId, "schedule.create", AuditOutcome.Ok,
            $"Schedule \"{name}\" ({schedule.Id}): {type}, {ScheduleRunner.DescribeInterval(minutes)}.", schedule.EngagementId, path, ct: ct);
        return Results.Json(Describe(schedule), statusCode: 201);
    }

    private static async Task<IResult> Update(
        string id, UpdateScheduleRequest body, HttpContext http, CerberusDbContext db, AuditLog audit, TimeProvider clock, CancellationToken ct)
    {
        var userId = UserId(http);
        var schedule = await db.Schedules.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (schedule is null) return NotFound();
        var now = clock.GetUtcNow().UtcDateTime;

        if (body.Name is not null)
        {
            if (Cerberus.Api.Chat.SessionTitler.CleanManual(body.Name) is not { } name) return Bad("name must be one line of at most 100 characters.");
            schedule.Name = name;
        }
        if (body.Every is not null)
        {
            if (ScheduleRunner.ParseInterval(body.Every) is not { } minutes)
            {
                return Bad($"every must be between {ScheduleRunner.MinIntervalMinutes} minutes and 30 days.");
            }
            schedule.IntervalMinutes = minutes;
            schedule.NextRunAt = now.AddMinutes(minutes);
        }
        if (body.Enabled is { } enabled && enabled != schedule.Enabled)
        {
            schedule.Enabled = enabled;
            if (enabled)
            {
                // Resumed: start counting afresh, and do not fire at once for the time it was paused.
                schedule.Refusals = 0;
                schedule.NextRunAt = ScheduleRunner.NextAfter(StoredTime.Utc(schedule.NextRunAt), schedule.IntervalMinutes, now);
            }
        }
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(userId, "schedule.update", AuditOutcome.Ok,
            $"Schedule \"{schedule.Name}\" ({schedule.Id}): {(schedule.Enabled ? "on" : "paused")}, {ScheduleRunner.DescribeInterval(schedule.IntervalMinutes)}.",
            schedule.EngagementId, schedule.TargetPath, ct: ct);
        return Results.Json(Describe(schedule));
    }

    private static async Task<IResult> Delete(string id, HttpContext http, CerberusDbContext db, AuditLog audit, CancellationToken ct)
    {
        var userId = UserId(http);
        var schedule = await db.Schedules.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (schedule is null) return NotFound();
        db.Schedules.Remove(schedule);
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(userId, "schedule.delete", AuditOutcome.Ok, $"Schedule \"{schedule.Name}\" ({schedule.Id}) removed.",
            schedule.EngagementId, schedule.TargetPath, ct: ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RunNow(
        string id, HttpContext http, CerberusDbContext db, ScheduleRunner runner, TimeProvider clock, CancellationToken ct)
    {
        var userId = UserId(http);
        if (!await db.Schedules.AnyAsync(s => s.Id == id && s.UserId == userId, ct)) return NotFound();
        var firing = await runner.RunNowAsync(id, clock.GetUtcNow().UtcDateTime, ct);
        return firing is null ? NotFound() : Results.Json(new { outcome = firing.Kind, detail = firing.Detail, jobId = firing.JobId });
    }
}
