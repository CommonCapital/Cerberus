using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Cerberus.Api.Auth;
using StackExchange.Redis;

namespace Cerberus.Api.Cluster;

/// <param name="Url">Where Redis is: <c>redis://[user:password@]host[:port]</c>, or <c>rediss://</c> for TLS (CERBERUS_REDIS_URL). Never logged or shown.</param>
public sealed record RedisOptions(string? Url)
{
    /// <summary>Everything of ours in Redis starts with this, so one Redis can serve other things too.</summary>
    public const string Prefix = "cerberus:";

    public bool Enabled => !string.IsNullOrWhiteSpace(Url);

    /// <summary>Where it is, for a log line or an operator: host and port, nothing that is a secret.</summary>
    public string Where => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? $"{uri.Host}:{(uri.Port > 0 ? uri.Port : 6379)}" : "an address that cannot be read";

    /// <summary>
    /// The connection settings. A Redis that is not there yet does not stop the service from starting: the
    /// client keeps trying, and readiness says what is missing meanwhile.
    /// </summary>
    public ConfigurationOptions Configuration()
    {
        var uri = new Uri(Url ?? throw new InvalidOperationException("No Redis address is configured."));
        var settings = new ConfigurationOptions
        {
            AbortOnConnectFail = false,
            ClientName = "cerberus",
            ConnectTimeout = 5000,
            Ssl = uri.Scheme == "rediss",
        };
        settings.EndPoints.Add(uri.Host, uri.Port > 0 ? uri.Port : 6379);
        var credentials = uri.UserInfo.Split(':', 2);
        if (credentials.Length == 2)
        {
            if (credentials[0].Length > 0) settings.User = Uri.UnescapeDataString(credentials[0]);
            settings.Password = Uri.UnescapeDataString(credentials[1]);
        }
        return settings;
    }
}

/// <summary>The one connection to Redis that signals, places and counters share. Opened on first use.</summary>
public sealed class RedisLink(RedisOptions options) : IAsyncDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> _connection = new(() => ConnectionMultiplexer.ConnectAsync(options.Configuration()));

    public Task<ConnectionMultiplexer> ConnectionAsync() => _connection.Value;

    public async Task<IDatabase> DatabaseAsync() => (await ConnectionAsync()).GetDatabase();

    /// <summary>Null when Redis answers; otherwise what is wrong, in a line without the address.</summary>
    public async Task<string?> ProblemAsync(CancellationToken ct)
    {
        try
        {
            var connection = await ConnectionAsync().WaitAsync(ct);
            if (!connection.IsConnected) return "Redis cannot be reached; reconnecting";
            await connection.GetDatabase().PingAsync().WaitAsync(ct);
            return null;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return "Redis cannot be reached";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsValueCreated)
        {
            try
            {
                await (await _connection.Value).DisposeAsync();
            }
            catch (Exception ex) when (ex is RedisException or ObjectDisposedException)
            {
                // Leaving: a Redis that is already gone is no reason to fail.
            }
        }
    }
}

/// <summary>
/// The sign-in throttle counted in Redis: five wrong passwords lock an account whichever instances they
/// were tried on — counted per process, a guesser would get five tries for every instance. The account's
/// address is stored as a hash. If Redis cannot be asked, this instance counts on its own until it can:
/// a throttle that fails open would be worse than one that counts apart for a while.
/// </summary>
public sealed class RedisLoginThrottle(RedisLink link, ILogger<RedisLoginThrottle> logger) : ILoginThrottle
{
    private readonly LoginThrottle _alone = new();

    private static string Hash(string email) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email.Trim().ToLowerInvariant())));

    private static RedisKey Failures(string email) => $"{RedisOptions.Prefix}login:failures:{Hash(email)}";
    private static RedisKey Locked(string email) => $"{RedisOptions.Prefix}login:locked:{Hash(email)}";

    private IDatabase? Database()
    {
        try
        {
            var connection = link.ConnectionAsync().GetAwaiter().GetResult();
            return connection.IsConnected ? connection.GetDatabase() : null;
        }
        catch (RedisException)
        {
            return null;
        }
    }

    public TimeSpan? LockedFor(string email)
    {
        if (_alone.LockedFor(email) is { } here) return here;
        try
        {
            return Database()?.KeyTimeToLive(Locked(email)) is { } left && left > TimeSpan.Zero ? left : null;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return null;   // counted here alone meanwhile (above)
        }
    }

    public bool Failed(string email)
    {
        // Always counted here too: the fallback is then already warm when Redis goes away.
        var lockedHere = _alone.Failed(email);
        try
        {
            if (Database() is not { } db) return lockedHere;
            var failures = db.StringIncrement(Failures(email));
            if (failures == 1) db.KeyExpire(Failures(email), LoginThrottle.Window);
            if (failures < LoginThrottle.MaxFailures) return false;
            db.KeyDelete(Failures(email));
            // Only the failure that sets the lock reports it, so it is written to the audit log once.
            return db.StringSet(Locked(email), "1", LoginThrottle.Lock, When.NotExists);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning("The sign-in count could not be shared ({Problem}); counting on this instance alone.", ex.GetType().Name);
            return lockedHere;
        }
    }

    public void Succeeded(string email)
    {
        _alone.Succeeded(email);
        try
        {
            Database()?.KeyDelete([Failures(email), Locked(email)]);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // The count runs out on its own.
        }
    }
}

/// <summary>Redis, where it is configured: the instances cannot keep order among themselves without it.</summary>
public sealed class RedisCheck(RedisLink link, OrphanSweeper instances) : Cerberus.Api.Health.IReadinessCheck
{
    public string Name => "redis";
    public bool Required => true;

    public async Task<(bool, string)> CheckAsync(CancellationToken ct) =>
        await link.ProblemAsync(ct) is { } problem
            ? (false, $"{problem}; chat turns wait, and stops and answers do not reach other instances")
            : (true, $"connected; {await instances.AliveAsync(ct)} instance(s) of the service alive");
}
