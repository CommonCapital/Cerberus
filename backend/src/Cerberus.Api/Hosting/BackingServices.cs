using System.Diagnostics;
using System.Globalization;
using Cerberus.Api.Health;

namespace Cerberus.Api.Hosting;

/// <summary>One service the API can use when it runs and do without when it does not (PostgreSQL, Redis, RabbitMQ).</summary>
public interface IServiceProbe
{
    /// <summary>What it is, for a person: "PostgreSQL".</summary>
    string Name { get; }

    /// <summary>Its setting, e.g. CERBERUS_POSTGRES_URL. Unset means not wanted, and it is not even tried.</summary>
    string Variable { get; }

    bool Configured { get; }

    /// <summary>Host and port (and database) — never a user name or a password.</summary>
    string Where { get; }

    /// <summary>What does the work when this service is not used: "the local SQLite file".</summary>
    string StandIn { get; }

    /// <summary>Returns when the service answered and can be used; throws otherwise.</summary>
    Task ProbeAsync(CancellationToken ct);
}

/// <param name="Problem">Why it is not used although configured: one line, without secrets.</param>
public sealed record ServiceChoice(string Name, string Variable, bool Configured, bool InUse, string Where, string StandIn, string? Problem)
{
    public bool FellBack => Configured && !InUse;

    /// <summary>The line written at start-up and shown by the readiness check.</summary>
    public string Describe() =>
        InUse ? $"{Name} at {Where}: in use"
        : Configured ? $"{Name} at {Where} {Problem}; using {StandIn} instead until the service is restarted"
        : $"{Name}: not configured ({Variable}); using {StandIn}";
}

/// <summary>
/// What the API runs on, decided once at start-up: each configured service that answers within a short
/// wait is used, and each one that does not is replaced by its local stand-in — the service starts either
/// way. So the same installation runs alone on a laptop and over PostgreSQL, Redis and RabbitMQ when they
/// are there. With <see cref="RequireVariable"/> set to "true", a configured service that does not answer
/// stops the start instead (several copies of the service must not each fall back on their own).
/// </summary>
public sealed class BackingServices(IReadOnlyList<ServiceChoice> choices)
{
    public const string RequireVariable = "CERBERUS_REQUIRE_SERVICES";
    public const string WaitVariable = "CERBERUS_SERVICE_WAIT_SECONDS";
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);

    /// <summary>How long one attempt may take; a service that is down usually says so at once.</summary>
    public static readonly TimeSpan AttemptLimit = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan RetryEvery = TimeSpan.FromMilliseconds(500);

    public IReadOnlyList<ServiceChoice> Choices { get; } = choices;

    public ServiceChoice this[string name] => Choices.Single(c => c.Name == name);

    public bool Uses(string name) => Choices.Any(c => c.Name == name && c.InUse);

    public IReadOnlyList<ServiceChoice> FellBack => Choices.Where(c => c.FellBack).ToList();

    /// <summary>The same choices, with one service that answered found unusable after all.</summary>
    public BackingServices WithFallback(string name, string problem) =>
        new(Choices.Select(c => c.Name == name ? c with { InUse = false, Problem = problem } : c).ToList());

    public static TimeSpan WaitFrom(Func<string, string?> env) =>
        double.TryParse(env(WaitVariable), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds) : DefaultWait;

    /// <summary>
    /// Tries every configured service at the same time, again and again until it answers or <paramref name="wait"/>
    /// has passed (at least once each), so a service starting next to the API is not missed by a second.
    /// </summary>
    public static async Task<BackingServices> DecideAsync(IEnumerable<IServiceProbe> probes, TimeSpan wait, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var choices = await Task.WhenAll(probes.Select(probe => DecideAsync(probe, clock, wait, ct)));
        return new BackingServices(choices);
    }

    private static async Task<ServiceChoice> DecideAsync(IServiceProbe probe, Stopwatch clock, TimeSpan wait, CancellationToken ct)
    {
        if (!probe.Configured) return new ServiceChoice(probe.Name, probe.Variable, false, false, "", probe.StandIn, null);
        string problem;
        while (true)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(AttemptLimit);
            try
            {
                await probe.ProbeAsync(attempt.Token).WaitAsync(attempt.Token);
                return new ServiceChoice(probe.Name, probe.Variable, true, true, probe.Where, probe.StandIn, null);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                problem = $"did not answer within {AttemptLimit.TotalSeconds:0} s";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problem = Explain(ex);
            }
            if (clock.Elapsed + RetryEvery >= wait) break;
            await Task.Delay(RetryEvery, ct);
        }
        return new ServiceChoice(probe.Name, probe.Variable, true, false, probe.Where, probe.StandIn, problem);
    }

    /// <summary>
    /// What went wrong, in words an operator can act on. Built from the kind of failure, never from the
    /// exception's own message, which may hold an address with a user name or the server's reply.
    /// </summary>
    public static string Explain(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case Npgsql.PostgresException { SqlState: "28P01" or "28000" }:
                case RabbitMQ.Client.Exceptions.AuthenticationFailureException:
                    return "refused the user name or password";
                case Npgsql.PostgresException { SqlState: "42501" }:
                    return "refused: the user may not create the database or its tables";
                case Npgsql.PostgresException pg:
                    return $"answered with an error (SQL state {pg.SqlState})";
                case StackExchange.Redis.RedisServerException when e.Message.Contains("WRONGPASS", StringComparison.Ordinal) || e.Message.Contains("NOAUTH", StringComparison.Ordinal):
                    return "refused the user name or password";
                case System.Net.Sockets.SocketException:
                case StackExchange.Redis.RedisConnectionException:
                case RabbitMQ.Client.Exceptions.BrokerUnreachableException:
                case Npgsql.NpgsqlException { InnerException: null or System.Net.Sockets.SocketException or TimeoutException or IOException }:
                    return "cannot be reached";
                case TimeoutException:
                    return $"did not answer within {AttemptLimit.TotalSeconds:0} s";
            }
        }
        return $"cannot be used ({ex.GetType().Name})";
    }
}

/// <summary>What the API runs on: each service that is in use, and each that was configured but stood in for.</summary>
public sealed class BackingServicesCheck(BackingServices backing) : IReadinessCheck
{
    public string Name => "services";

    /// <summary>A fallback is a choice that works, not a fault that stops work: it shows as degraded.</summary>
    public bool Required => false;

    public Task<(bool Ok, string Detail)> CheckAsync(CancellationToken ct)
    {
        // The readiness answer is public: host and port are left out, as in the other checks.
        var parts = backing.Choices.Select(c =>
            c.InUse ? $"{c.Name} in use"
            : c.Configured ? $"{c.Name} configured but {c.Problem} at start-up, so {c.StandIn} is used"
            : $"{c.StandIn} (no {c.Name} configured)");
        return Task.FromResult((backing.FellBack.Count == 0, string.Join("; ", parts)));
    }
}
