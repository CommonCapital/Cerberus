using System.Globalization;
using System.Threading.RateLimiting;
using Cerberus.Api.Security;
using StackExchange.Redis;

namespace Cerberus.Api.Cluster;

/// <param name="Allowed">The request may go ahead; it has been counted.</param>
/// <param name="RetryAfter">When refused: how long until the window has room again, roughly.</param>
public sealed record RateDecision(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// The request-rate limits (see <see cref="ServiceHardening"/>) counted in Redis, so that they hold over every
/// instance of the service (PLAN.md §22 item 8): counted per instance, a client would get the limit once per
/// instance. The same sliding minute as in one process — six segments of ten seconds — taken by one script, so
/// that two instances cannot both let in the last request, and on Redis's own clock, so that the instances'
/// clocks need not agree. A request is counted only if both its address and its token have room; a refused one
/// is not counted. If Redis cannot be asked, this instance counts on its own until it can: a limit that
/// disappears when Redis does would be worse than one that is counted apart for a while.
/// </summary>
public sealed class SharedRateLimit(RedisLink link, ServiceLimits limits, ILogger<SharedRateLimit> logger, TimeSpan? segment = null)
{
    public const int Segments = 6;

    /// <summary>The longest a request waits for Redis to count it; slower than that, this instance counts it itself.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan _segment = segment ?? TimeSpan.FromSeconds(10);
    private readonly PartitionedRateLimiter<HttpContext> _alone = ServiceHardening.LocalLimiter(limits);
    private int _warned;

    // ARGV: 1 segment length (ms), 2 number of segments, then per limit: prefix and permits ("" and 0 for none).
    // Returns 0 when allowed, else the milliseconds until the oldest segment of the window that is full rolls off.
    private const string Take =
        """
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        local length = tonumber(ARGV[1])
        local segments = tonumber(ARGV[2])
        local current = math.floor(now / length)
        local wait = 0
        for i = 3, #ARGV, 2 do
          local prefix, permits = ARGV[i], tonumber(ARGV[i + 1])
          if permits > 0 then
            local total = 0
            for s = 0, segments - 1 do
              total = total + tonumber(redis.call('GET', prefix .. ':' .. (current - s)) or '0')
            end
            if total >= permits then
              local roll = (current + 1) * length - now
              if roll > wait then wait = roll end
            end
          end
        end
        if wait > 0 then return wait end
        for i = 3, #ARGV, 2 do
          local prefix, permits = ARGV[i], tonumber(ARGV[i + 1])
          if permits > 0 then
            local key = prefix .. ':' .. current
            redis.call('INCR', key)
            redis.call('PEXPIRE', key, length * (segments + 1))
          end
        end
        return 0
        """;

    public async Task<RateDecision> DecideAsync(HttpContext http)
    {
        var (address, user) = ServiceHardening.Keys(http);
        var perAddress = address is null ? 0 : limits.RequestsPerMinutePerAddress;
        var perUser = user is null ? 0 : limits.RequestsPerMinutePerUser;
        if (perAddress <= 0 && perUser <= 0) return new RateDecision(true, TimeSpan.Zero);

        try
        {
            var db = link.DatabaseIfUp() ?? throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Redis is not connected.");
            var wait = (long)await db.ScriptEvaluateAsync(Take, keys: null, values:
            [
                (long)_segment.TotalMilliseconds, Segments,
                $"{RedisOptions.Prefix}rate:{address}", perAddress,
                $"{RedisOptions.Prefix}rate:{user}", perUser,
            ]).WaitAsync(Patience);
            Interlocked.Exchange(ref _warned, 0);
            return wait <= 0 ? new RateDecision(true, TimeSpan.Zero) : new RateDecision(false, TimeSpan.FromMilliseconds(wait));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            if (Interlocked.Exchange(ref _warned, 1) == 0)
            {
                logger.LogWarning("Request rates cannot be counted in Redis ({Problem}); this instance counts on its own until it can.", ex.GetType().Name);
            }
            using var lease = _alone.AttemptAcquire(http);
            return lease.IsAcquired
                ? new RateDecision(true, TimeSpan.Zero)
                : new RateDecision(false, lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>In the pipeline where the in-process limiter would be: refuses with 429 and Retry-After, as it does.</summary>
    public static void Use(WebApplication app)
    {
        var shared = app.Services.GetRequiredService<SharedRateLimit>();
        app.Use(async (context, next) =>
        {
            var decision = await shared.DecideAsync(context);
            if (decision.Allowed)
            {
                await next(context);
                return;
            }
            await ServiceHardening.RefuseAsync(context, decision.RetryAfter, context.RequestAborted);
        });
    }
}
