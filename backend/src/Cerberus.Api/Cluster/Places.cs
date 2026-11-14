using StackExchange.Redis;

namespace Cerberus.Api.Cluster;

/// <summary>
/// Places that are handed out per key: one per conversation, a few per user (PLAN.md §22 items 4 and 7).
/// Whoever comes when they are all taken waits. With one instance they are counted in the process; with
/// several, in Redis, so that the limit holds over all of them.
/// </summary>
public interface IPlaces
{
    /// <summary>Waits for one of <paramref name="places"/> places under <paramref name="key"/>. Dispose the result to give it back.</summary>
    Task<IAsyncDisposable> EnterAsync(string key, int places, CancellationToken ct);
}

/// <summary>One instance: semaphores in memory, served in the order of arrival.</summary>
public sealed class LocalPlaces : IPlaces
{
    private readonly Dictionary<string, (SemaphoreSlim Gate, int Holders)> _gates = [];

    public async Task<IAsyncDisposable> EnterAsync(string key, int places, CancellationToken ct)
    {
        SemaphoreSlim gate;
        lock (_gates)
        {
            var entry = _gates.TryGetValue(key, out var known) ? known : (Gate: new SemaphoreSlim(places, places), Holders: 0);
            _gates[key] = (entry.Gate, entry.Holders + 1);
            gate = entry.Gate;
        }
        try
        {
            await gate.WaitAsync(ct);
        }
        catch
        {
            Leave(key, release: false);
            throw;
        }
        return new Place(this, key);
    }

    private void Leave(string key, bool release)
    {
        lock (_gates)
        {
            var (gate, holders) = _gates[key];
            if (release) gate.Release();
            if (holders <= 1) _gates.Remove(key);
            else _gates[key] = (gate, holders - 1);
        }
    }

    private sealed class Place(LocalPlaces owner, string key) : IAsyncDisposable
    {
        private int _left;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0) owner.Leave(key, release: true);
            return ValueTask.CompletedTask;
        }
    }
}

/// <param name="HoldFor">How long a place stays taken without its holder renewing it — what a crashed holder costs the others.</param>
/// <param name="AskEvery">How often a waiter looks whether a place has come free.</param>
public sealed record RedisPlacesOptions(TimeSpan? HoldFor = null, TimeSpan? AskEvery = null)
{
    public TimeSpan Hold => HoldFor ?? TimeSpan.FromSeconds(30);
    public TimeSpan Ask => AskEvery ?? TimeSpan.FromMilliseconds(100);
}

/// <summary>
/// Several instances: the holders of a key's places are a sorted set in Redis, each with the time its hold
/// runs out. Taking a place is one script — forget the holds that ran out, and join if there is room — so two
/// instances cannot both take the last one. A holder renews its hold while it works and removes it when done;
/// a holder that died stops renewing and its place comes free by itself. Time is Redis's own, so the
/// instances' clocks need not agree.
/// <para>
/// What it does not promise: the order of waiters (they ask again every little while; the conversation's
/// order is kept by the worker's own check), and a place cannot be told that it was lost — an instance cut
/// off from Redis for longer than a hold lasts may work on while another has taken its place.
/// </para>
/// </summary>
public sealed class RedisPlaces(RedisLink link, ILogger<RedisPlaces> logger, RedisPlacesOptions? options = null) : IPlaces
{
    private readonly RedisPlacesOptions _options = options ?? new();
    private readonly RedisLink _link = link;

    // KEYS[1] the set of holders. ARGV: 1 how long a hold lasts (ms), 2 how many places, 3 this holder.
    private const string Take =
        """
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now)
        if redis.call('ZSCORE', KEYS[1], ARGV[3]) or redis.call('ZCARD', KEYS[1]) < tonumber(ARGV[2]) then
          redis.call('ZADD', KEYS[1], now + tonumber(ARGV[1]), ARGV[3])
          redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[1]) * 2)
          return 1
        end
        return 0
        """;

    // Renewing must not bring back a hold that ran out and was given to somebody else.
    private const string Renew =
        """
        local t = redis.call('TIME')
        local now = t[1] * 1000 + math.floor(t[2] / 1000)
        local until_ = redis.call('ZSCORE', KEYS[1], ARGV[2])
        if until_ and tonumber(until_) > now then
          redis.call('ZADD', KEYS[1], now + tonumber(ARGV[1]), ARGV[2])
          redis.call('PEXPIRE', KEYS[1], tonumber(ARGV[1]) * 2)
          return 1
        end
        return 0
        """;

    private static RedisKey Key(string key) => $"{RedisOptions.Prefix}places:{key}";

    public async Task<IAsyncDisposable> EnterAsync(string key, int places, CancellationToken ct)
    {
        var holder = Guid.NewGuid().ToString("n");
        var hold = (long)_options.Hold.TotalMilliseconds;
        var warned = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var db = await _link.DatabaseAsync();
                if ((long)await db.ScriptEvaluateAsync(Take, [Key(key)], [hold, Math.Max(1, places), holder]) == 1)
                {
                    return new Place(this, key, holder);
                }
                await Task.Delay(_options.Ask + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 30)), ct);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                // Without Redis the places cannot be counted, so nobody is let in: waiting is the safe side.
                if (!warned) logger.LogWarning("Places cannot be counted while Redis is away ({Problem}); waiting.", ex.GetType().Name);
                warned = true;
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    private sealed class Place : IAsyncDisposable
    {
        private readonly RedisPlaces _owner;
        private readonly string _key;
        private readonly string _holder;
        private readonly CancellationTokenSource _done = new();
        private readonly Task _renewing;

        public Place(RedisPlaces owner, string key, string holder)
        {
            (_owner, _key, _holder) = (owner, key, holder);
            _renewing = RenewAsync();
        }

        private async Task RenewAsync()
        {
            var every = TimeSpan.FromMilliseconds(_owner._options.Hold.TotalMilliseconds / 3);
            try
            {
                while (true)
                {
                    await Task.Delay(every, _done.Token);
                    try
                    {
                        var db = await _owner._link.DatabaseAsync();
                        await db.ScriptEvaluateAsync(Renew, [Key(_key)], [(long)_owner._options.Hold.TotalMilliseconds, _holder]);
                    }
                    catch (Exception ex) when (ex is RedisException or TimeoutException)
                    {
                        // Tried again at the next renewal; if Redis stays away, the hold runs out.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Given back.
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_done.IsCancellationRequested) return;
            _done.Cancel();
            await _renewing;
            try
            {
                await (await _owner._link.DatabaseAsync()).SortedSetRemoveAsync(Key(_key), _holder);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                // It runs out by itself.
            }
            _done.Dispose();
        }
    }
}
