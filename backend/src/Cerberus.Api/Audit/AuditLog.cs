using Cerberus.Authorization;
using Cerberus.Data;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Audit;

/// <summary>
/// Appends to the per-user, hash-chained audit log. Uses its own
/// short-lived DbContext so an audit entry is committed on its own — it never
/// rides along with, or is rolled back by, the request's other changes.
/// </summary>
public sealed class AuditLog(IServiceScopeFactory scopes, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    // One writer at a time keeps each user's sequence gap-free in this process;
    // the unique (UserId, Sequence) index is the backstop across processes.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private const int MaxDetailLength = 2000;

    public async Task AppendAsync(
        string userId, string action, string outcome, string detail,
        string? engagementId = null, string? target = null, CapabilityTier? tier = null,
        CancellationToken ct = default)
    {
        if (detail.Length > MaxDetailLength) detail = detail[..MaxDetailLength] + "…";

        await Gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();

            var last = await db.AuditEvents.AsNoTracking()
                .Where(a => a.UserId == userId)
                .OrderByDescending(a => a.Sequence)
                .Select(a => new { a.Sequence, a.Hash })
                .FirstOrDefaultAsync(ct);

            var previousHash = last?.Hash ?? AuditChain.Genesis;
            var now = _clock.GetUtcNow().UtcDateTime;
            // PostgreSQL timestamps retain microseconds, not .NET's 100ns
            // ticks. Hash exactly what both databases can round-trip. Only
            // new entries are normalized: existing hashes still cover every
            // stored tick, including older SQLite entries at full precision.
            var createdAt = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMicrosecond));
            var entry = new AuditEvent
            {
                UserId = userId,
                Sequence = (last?.Sequence ?? 0) + 1,
                EngagementId = engagementId,
                Action = action,
                Target = target,
                Tier = tier?.ToString(),
                Outcome = outcome,
                Detail = detail,
                CreatedAt = createdAt,
                PreviousHash = previousHash,
            };
            db.AuditEvents.Add(entry);
            // SaveChanges replaces NUL characters for PostgreSQL. Apply that
            // same storage rule before hashing, while this isolated context
            // contains only the new entry, so persistence cannot alter its hash.
            if (db.IsPostgres) PostgresStorage.CleanText(db.ChangeTracker);
            entry.Hash = AuditChain.ComputeHash(ToChained(entry).Record, entry.PreviousHash);
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static ChainedAuditRecord ToChained(AuditEvent e) => new(
        new AuditRecord(e.Sequence, e.UserId, e.EngagementId, e.Action, e.Target, e.Tier, e.Outcome, e.Detail, e.CreatedAt),
        e.PreviousHash, e.Hash);
}

/// <summary>Outcome values recorded in the audit log.</summary>
public static class AuditOutcome
{
    public const string Allowed = "allowed";
    public const string Denied = "denied";
    public const string Ok = "ok";
    public const string Error = "error";
}
