using Cerberus.Api.Search;
using Cerberus.Compliance;
using Cerberus.Data;
using Cerberus.Scanning;
using Cerberus.Skills;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Health;

public enum HealthState
{
    Healthy,

    /// <summary>The service works, with something optional missing; the detail says what is affected.</summary>
    Degraded,

    /// <summary>Something every request depends on is missing. The service should not be sent work.</summary>
    Unhealthy,
}

/// <param name="Detail">One line for an operator. Never a path, an address with credentials, or anything else an anonymous caller should not read.</param>
public sealed record HealthResult(string Name, HealthState Status, string Detail);

/// <summary>One thing the service depends on.</summary>
public interface IReadinessCheck
{
    string Name { get; }

    /// <summary>Whether the service cannot work without it: a failure is then unhealthy, otherwise degraded.</summary>
    bool Required { get; }

    /// <summary>Null when all is well with an optional note, or throws / returns a problem.</summary>
    Task<(bool Ok, string Detail)> CheckAsync(CancellationToken ct);
}

public sealed record ReadinessReport(HealthState Status, IReadOnlyList<HealthResult> Checks, DateTime CheckedAt);

/// <summary>
/// Readiness (PLAN.md §21): whether the service can actually do its work,
/// dependency by dependency — as opposed to liveness (<c>/health</c>), which
/// only says the process answers. A required dependency that fails makes the
/// service unhealthy (503: do not route work here); an optional one makes it
/// degraded and says what is affected. Checks are bounded in time, a check
/// that throws is a failed check, and results are kept for a few seconds so
/// that a probe every second does not become load.
/// </summary>
public sealed class ReadinessService(IEnumerable<IReadinessCheck> checks, TimeProvider clock)
{
    /// <summary>How long one check may take before it counts as failed.</summary>
    public TimeSpan CheckTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ReadinessReport? _last;

    public async Task<ReadinessReport> CheckAsync(CancellationToken ct)
    {
        if (Fresh() is { } fresh) return fresh;
        await _gate.WaitAsync(ct);
        try
        {
            if (Fresh() is { } justMade) return justMade;
            var results = await Task.WhenAll(checks.Select(check => RunAsync(check, ct)));
            var status = results.Length == 0 ? HealthState.Healthy : results.Max(r => r.Status);
            return _last = new ReadinessReport(status, results, clock.GetUtcNow().UtcDateTime);
        }
        finally
        {
            _gate.Release();
        }
    }

    private ReadinessReport? Fresh() =>
        _last is { } last && clock.GetUtcNow().UtcDateTime - last.CheckedAt < KeepFor ? last : null;

    private async Task<HealthResult> RunAsync(IReadinessCheck check, CancellationToken ct)
    {
        var failed = check.Required ? HealthState.Unhealthy : HealthState.Degraded;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(CheckTimeout);
        try
        {
            var (ok, detail) = await check.CheckAsync(limit.Token).WaitAsync(limit.Token);
            return new HealthResult(check.Name, ok ? HealthState.Healthy : failed, detail);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new HealthResult(check.Name, failed, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"did not answer within {CheckTimeout.TotalSeconds:0.#} s"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type says enough for an operator; the message may hold a path or an address.
            return new HealthResult(check.Name, failed, $"failed ({ex.GetType().Name})");
        }
    }
}

/// <summary>The database answers and has its schema.</summary>
public sealed class DatabaseCheck(IServiceScopeFactory scopes) : IReadinessCheck
{
    public string Name => "database";
    public bool Required => true;

    public async Task<(bool, string)> CheckAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        if (!await db.Database.CanConnectAsync(ct)) return (false, "cannot be opened");
        await db.Users.AsNoTracking().Select(u => u.Id).FirstOrDefaultAsync(ct);   // throws if the schema is not there
        return (true, "reachable");
    }
}

/// <param name="KeyRing">Where the keys that encrypt users' API keys are kept.</param>
public sealed record HealthPaths(string KeyRing);

/// <summary>The key ring can be written: without it, users' API keys can neither be stored nor read.</summary>
public sealed class KeyRingCheck(HealthPaths paths) : IReadinessCheck
{
    public string Name => "key-ring";
    public bool Required => true;

    public Task<(bool, string)> CheckAsync(CancellationToken ct)
    {
        // The directory is the service's own; on a fresh installation nothing has made it yet.
        Directory.CreateDirectory(paths.KeyRing);
        var probe = Path.Combine(paths.KeyRing, $".ready-{Guid.NewGuid():n}");
        File.WriteAllBytes(probe, []);
        File.Delete(probe);
        return Task.FromResult((true, "writable"));
    }
}

/// <summary>The directory jobs are confined to exists: without it every scan and agent run fails.</summary>
public sealed class ScanRootCheck(ScanOptions options) : IReadinessCheck
{
    public string Name => "scan-root";
    public bool Required => true;

    public Task<(bool, string)> CheckAsync(CancellationToken ct) =>
        Task.FromResult(Directory.Exists(options.ScanRoot) ? (true, "present") : (false, "the scan root directory does not exist"));
}

/// <summary>What the background worker says about itself.</summary>
public sealed class WorkerHealth
{
    public DateTime? StartedAt { get; set; }

    /// <summary>Set if the worker's loop ended on an error: jobs would be accepted and never run.</summary>
    public string? Fault { get; set; }
}

public sealed class JobWorkerCheck(WorkerHealth worker) : IReadinessCheck
{
    public string Name => "job-worker";
    public bool Required => true;

    public Task<(bool, string)> CheckAsync(CancellationToken ct) =>
        Task.FromResult(
            worker.Fault is not null ? (false, "stopped on an error; jobs are not being run")
            : worker.StartedAt is null ? (false, "not started yet")
            : (true, "running"));
}

public sealed class CatalogsCheck(CatalogLoader catalogs) : IReadinessCheck
{
    public string Name => "catalogs";
    public bool Required => false;

    public Task<(bool, string)> CheckAsync(CancellationToken ct)
    {
        var count = catalogs.All.Count;
        return Task.FromResult(count > 0 ? (true, $"{count} loaded") : (false, "none loaded; compliance assessments are unavailable"));
    }
}

public sealed class SkillsCheck(SkillLibrary skills) : IReadinessCheck
{
    public string Name => "skills";
    public bool Required => false;

    public Task<(bool, string)> CheckAsync(CancellationToken ct)
    {
        var count = skills.Count;
        return Task.FromResult(count > 0 ? (true, $"{count} loaded") : (false, "none loaded; agents work without the skills library"));
    }
}

/// <summary>The embedding model: optional — without it everything works except semantic code search.</summary>
public sealed class EmbeddingsCheck(IEmbedder embedder) : IReadinessCheck
{
    public string Name => "embeddings";
    public bool Required => false;

    public async Task<(bool, string)> CheckAsync(CancellationToken ct) =>
        await embedder.ProblemAsync(ct) is { } problem
            ? (false, $"{problem}; semantic code search is unavailable")
            : (true, $"{embedder.Model} available");
}

/// <summary>
/// The configured MCP servers: optional. Reports what is already known and never reaches out itself —
/// a readiness probe must not become traffic to other people's servers. Names and counts only.
/// </summary>
public sealed class McpCheck(Web.IMcpServers servers) : IReadinessCheck
{
    public string Name => "mcp";
    public bool Required => false;

    public Task<(bool, string)> CheckAsync(CancellationToken ct)
    {
        if (servers.Problems.Count > 0)
        {
            return Task.FromResult((false, $"{servers.Problems.Count} problem(s) in the MCP configuration (run mcp-check to see them); {servers.Names.Count} server(s) usable"));
        }
        if (servers.Names.Count == 0) return Task.FromResult((true, "no servers configured"));
        var down = servers.Known.Where(s => !s.Reachable).Select(s => s.Name).ToList();
        return Task.FromResult(down.Count > 0
            ? (false, $"{servers.Names.Count} configured; not reachable when last tried: {string.Join(", ", down)}")
            : (true, $"{servers.Names.Count} configured"));
    }
}

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        // Liveness: the process answers. Deliberately checks nothing — a slow database must not get the process restarted.
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));

        // Readiness: can it do its work? 503 only when something every request needs is missing.
        app.MapGet("/health/ready", async (ReadinessService readiness, CancellationToken ct) =>
        {
            var report = await readiness.CheckAsync(ct);
            return Results.Json(new
            {
                status = report.Status.ToString().ToLowerInvariant(),
                checkedAt = report.CheckedAt,
                checks = report.Checks.Select(c => new { name = c.Name, status = c.Status.ToString().ToLowerInvariant(), detail = c.Detail }),
            }, statusCode: report.Status == HealthState.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
        });
    }

    public static IServiceCollection AddReadiness(this IServiceCollection services, string keyRingPath)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new HealthPaths(keyRingPath));
        services.AddSingleton<WorkerHealth>();
        services.AddSingleton<IReadinessCheck, DatabaseCheck>();
        services.AddSingleton<IReadinessCheck, KeyRingCheck>();
        services.AddSingleton<IReadinessCheck, ScanRootCheck>();
        services.AddSingleton<IReadinessCheck, JobWorkerCheck>();
        services.AddSingleton<IReadinessCheck, CatalogsCheck>();
        services.AddSingleton<IReadinessCheck, SkillsCheck>();
        services.AddSingleton<IReadinessCheck, EmbeddingsCheck>();
        services.AddSingleton<ReadinessService>();
        return services;
    }
}
