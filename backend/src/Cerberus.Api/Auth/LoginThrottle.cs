using System.Collections.Concurrent;

namespace Cerberus.Api.Auth;

/// <summary>
/// Slows down password guessing: after a number of failed sign-ins in a row for
/// one account, further attempts are refused for a while — also with the right
/// password, so a guesser learns nothing during the lock. Counted per account
/// name, in memory; a restart clears it.
/// </summary>
public sealed class LoginThrottle(Func<DateTime>? clock = null)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan Lock = TimeSpan.FromMinutes(5);

    /// <summary>Accounts tracked at once; beyond it the oldest entries are dropped, so made-up names cannot fill memory.</summary>
    private const int MaxTracked = 10_000;

    private sealed record Entry(int Failures, DateTime FirstFailure, DateTime? LockedUntil);

    private readonly Func<DateTime> _now = clock ?? (() => DateTime.UtcNow);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private static string Key(string email) => email.Trim().ToLowerInvariant();

    /// <summary>How long sign-in for this account is still refused, or null if it may be tried.</summary>
    public TimeSpan? LockedFor(string email)
    {
        if (_entries.TryGetValue(Key(email), out var entry) && entry.LockedUntil is { } until && until > _now())
        {
            return until - _now();
        }
        return null;
    }

    /// <summary>Records a failed sign-in. Returns true when this failure locked the account.</summary>
    public bool Failed(string email)
    {
        var now = _now();
        var locked = false;
        _entries.AddOrUpdate(Key(email),
            _ => new Entry(1, now, null),
            (_, entry) =>
            {
                // A lock that ran out, or failures too far apart, start a new count.
                if (entry.LockedUntil is { } until ? until <= now : now - entry.FirstFailure > Window) return new Entry(1, now, null);
                var failures = entry.Failures + 1;
                if (failures < MaxFailures) return entry with { Failures = failures };
                locked = true;
                return entry with { Failures = failures, LockedUntil = now + Lock };
            });

        if (_entries.Count > MaxTracked)
        {
            foreach (var stale in _entries.OrderBy(e => e.Value.FirstFailure).Take(_entries.Count - MaxTracked).ToList())
            {
                _entries.TryRemove(stale.Key, out _);
            }
        }
        return locked;
    }

    /// <summary>A successful sign-in clears the count.</summary>
    public void Succeeded(string email) => _entries.TryRemove(Key(email), out _);
}
