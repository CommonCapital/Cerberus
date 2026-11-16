using System.Security.Cryptography;
using System.Text;
using Cerberus.Data;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Auth;

/// <summary>Public metadata: neither the bearer value nor its full digest can be read back.</summary>
public sealed record ApiTokenInfo(
    string Id, string Name, string TokenPrefix, DateTime CreatedAt, DateTime ExpiresAt, DateTime? RevokedAt);

/// <summary>The bearer value is returned once, by creation only. Do not log or audit this object.</summary>
public sealed record CreatedApiToken(string Token, ApiTokenInfo Info);

/// <summary>
/// Personal credentials for pipelines. They have the owner's ordinary access, not an independent role.
/// Management uses the owner's filtered context. Authentication alone may look up a full secret hash
/// before the owner is known; validation never writes, so polling does not create SQLite write contention.
/// </summary>
public sealed class ApiTokenService(CerberusDbContext db, TimeProvider? clock = null)
{
    public const int DefaultExpiryDays = 90;
    public const int MinExpiryDays = 1;
    public const int MaxExpiryDays = 365;
    public const int MaxNameLength = 100;
    public const string Prefix = "cerb_";
    private const int SecretBytes = 32;
    private const int TokenLength = 48; // "cerb_" plus 43 unpadded base64url characters (256 random bits).
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<CreatedApiToken> CreateAsync(
        string userId, string name, int? expiresInDays = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var cleanName = name?.Trim();
        if (string.IsNullOrEmpty(cleanName) || cleanName.Length > MaxNameLength || name!.Any(char.IsControl))
            throw new ArgumentException($"name must contain 1–{MaxNameLength} characters and no control characters", nameof(name));
        var days = expiresInDays ?? DefaultExpiryDays;
        if (days is < MinExpiryDays or > MaxExpiryDays)
            throw new ArgumentException($"expiresInDays must be between {MinExpiryDays} and {MaxExpiryDays}", nameof(expiresInDays));

        var raw = Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(SecretBytes))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var now = _clock.GetUtcNow().UtcDateTime;
        var row = new ApiToken
        {
            UserId = userId,
            Name = cleanName,
            TokenHash = Hash(raw),
            TokenPrefix = raw[..12],
            CreatedAt = now,
            ExpiresAt = now.AddDays(days),
        };
        db.ApiTokens.Add(row);
        await db.SaveChangesAsync(ct);
        return new CreatedApiToken(raw, Describe(row));
    }

    public async Task<IReadOnlyList<ApiTokenInfo>> ListAsync(string userId, CancellationToken ct = default)
    {
        var rows = await db.ApiTokens.AsNoTracking().Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id).ToListAsync(ct);
        return rows.Select(Describe).ToArray();
    }

    /// <summary>Idempotent for an existing owned token; false means it was not found for this owner.</summary>
    public async Task<bool> RevokeAsync(string userId, string id, CancellationToken ct = default)
    {
        var owned = db.ApiTokens.Where(t => t.UserId == userId && t.Id == id);
        var now = _clock.GetUtcNow().UtcDateTime;
        if (await owned.Where(t => t.RevokedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, (DateTime?)now), ct) != 0) return true;
        return await owned.AnyAsync(ct);
    }

    public async Task<string?> ValidateAsync(string raw, CancellationToken ct = default)
    {
        if (!IsApiToken(raw)) return null;
        var hash = Hash(raw);
        var now = _clock.GetUtcNow().UtcDateTime;
        // Authentication boundary: an HTTP request is restricted to no owner until this succeeds.
        // This exception is an exact, unique full-hash lookup, never a list or an operator-supplied id.
        return await db.ApiTokens.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now)
            .Select(t => t.UserId).SingleOrDefaultAsync(ct);
    }

    /// <summary>Logout proves possession of the entire bearer token and revokes only that credential.</summary>
    public async Task<bool> RevokeBearerAsync(string raw, CancellationToken ct = default)
    {
        if (!IsApiToken(raw)) return false;
        var hash = Hash(raw);
        var now = _clock.GetUtcNow().UtcDateTime;
        // As with validation, logout runs before an owner is known. Its full-hash predicate limits
        // this one bulk update; ordinary management must use RevokeAsync and its owner filter.
        return await db.ApiTokens.IgnoreQueryFilters()
            .Where(t => t.TokenHash == hash && t.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, (DateTime?)now), ct) != 0;
    }

    /// <summary>Only the new fixed-length credential format; legacy login tokens are 43 characters.</summary>
    public static bool IsApiToken(string? raw) => raw is { Length: TokenLength } &&
        raw.StartsWith(Prefix, StringComparison.Ordinal) &&
        raw.AsSpan(Prefix.Length).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_".AsSpan()) < 0;

    private static string Hash(string raw) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static ApiTokenInfo Describe(ApiToken row) => new(
        row.Id, row.Name, row.TokenPrefix, StoredTime.Utc(row.CreatedAt), StoredTime.Utc(row.ExpiresAt),
        row.RevokedAt is { } revoked ? StoredTime.Utc(revoked) : null);
}
