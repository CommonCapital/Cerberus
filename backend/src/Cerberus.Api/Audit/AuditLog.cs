using Cerberus.Authorization;
using Cerberus.Data;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Audit;

/// <summary>
/// Appends to the per-user, hash-chained audit log (PLAN.md §3). Uses its own
/// short-lived DbContext so an audit entry is committed on its own — it never
/// rides along with, or is rolled back by, the request's other changes.
/// </summary>
public sealed class AuditLog(IServiceScopeFactory scopes)
{
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
            var record = new AuditRecord(
                Sequence: (last?.Sequence ?? 0) + 1,
                UserId: userId,
                EngagementId: engagementId,
                Action: action,
                Target: target,
                Tier: tier?.ToString(),
                Outcome: outcome,
                Detail: detail,
                CreatedAt: DateTime.UtcNow);

            db.AuditEvents.Add(new AuditEvent
            {
                UserId = record.UserId,
                Sequence = record.Sequence,
                EngagementId = record.EngagementId,
                Action = record.Action,
                Target = record.Target,
                Tier = record.Tier,
                Outcome = record.Outcome,
                Detail = record.Detail,
                CreatedAt = record.CreatedAt,
                PreviousHash = previousHash,
                Hash = AuditChain.ComputeHash(record, previousHash),
            });
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
