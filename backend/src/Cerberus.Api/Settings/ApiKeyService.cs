using Cerberus.Core.Models;
using Cerberus.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Settings;

/// <summary>
/// Stores per-user model API keys encrypted at rest (ASP.NET Core Data
/// Protection) and resolves them into <see cref="ModelCredentials"/> for a
/// request. Keys are never returned in plaintext by the API.
/// </summary>
public sealed class ApiKeyService
{
    public static readonly string[] SupportedProviders = ["anthropic", "openai"];

    private readonly CerberusDbContext _db;
    private readonly IDataProtector _protector;

    public ApiKeyService(CerberusDbContext db, IDataProtectionProvider dataProtection)
    {
        _db = db;
        _protector = dataProtection.CreateProtector("Cerberus.ApiKeys.v1");
    }

    public static bool IsSupportedProvider(string provider) =>
        Array.IndexOf(SupportedProviders, provider) >= 0;

    public async Task SetAsync(string userId, string provider, string apiKey, CancellationToken ct = default)
    {
        var encrypted = _protector.Protect(apiKey);
        var existing = await _db.UserApiKeys
            .FirstOrDefaultAsync(k => k.UserId == userId && k.Provider == provider, ct);

        if (existing is null)
        {
            _db.UserApiKeys.Add(new UserApiKey { UserId = userId, Provider = provider, EncryptedKey = encrypted });
        }
        else
        {
            existing.EncryptedKey = encrypted;
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<string>> ListProvidersAsync(string userId, CancellationToken ct = default) =>
        await _db.UserApiKeys
            .Where(k => k.UserId == userId)
            .Select(k => k.Provider)
            .ToListAsync(ct);

    public async Task<bool> DeleteAsync(string userId, string provider, CancellationToken ct = default)
    {
        var existing = await _db.UserApiKeys
            .FirstOrDefaultAsync(k => k.UserId == userId && k.Provider == provider, ct);
        if (existing is null) return false;

        _db.UserApiKeys.Remove(existing);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Resolves a user's stored keys into credentials (decrypted in-memory only).</summary>
    public async Task<ModelCredentials> ResolveCredentialsAsync(string userId, CancellationToken ct = default)
    {
        var keys = await _db.UserApiKeys.Where(k => k.UserId == userId).ToListAsync(ct);

        string? anthropic = null;
        string? openai = null;
        foreach (var key in keys)
        {
            string decrypted;
            try
            {
                decrypted = _protector.Unprotect(key.EncryptedKey);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                continue; // key unreadable (e.g. keyring rotated) — skip
            }

            if (key.Provider == "anthropic") anthropic = decrypted;
            else if (key.Provider == "openai") openai = decrypted;
        }

        return new ModelCredentials(anthropic, openai, null);
    }
}
