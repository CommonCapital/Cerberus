using System.Security.Cryptography;
using Cerberus.Core.Security;
using Cerberus.Data;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Auth;

/// <summary>Local, self-hosted authentication.</summary>
public sealed class AuthService(CerberusDbContext db, ApiTokenService? apiTokens = null)
{
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(30);
    private readonly ApiTokenService _apiTokens = apiTokens ?? new ApiTokenService(db);

    public async Task<string> RegisterAsync(string email, string password, CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            throw new InvalidOperationException("A user with this email already exists");
        }

        var user = new User { Email = email, PasswordHash = PasswordHasher.Hash(password) };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return await CreateSessionAsync(user.Id, ct);
    }

    public async Task<string?> LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null || !PasswordHasher.Verify(password, user.PasswordHash))
        {
            return null;
        }
        return await CreateSessionAsync(user.Id, ct);
    }

    public Task<string?> ValidateTokenAsync(string token, CancellationToken ct = default) =>
        ApiTokenService.IsApiToken(token)
            ? _apiTokens.ValidateAsync(token, ct)
            : ValidateSessionTokenAsync(token, ct);

    /// <summary>Interactive login only: API tokens cannot create or manage other credentials.</summary>
    public async Task<string?> ValidateSessionTokenAsync(string token, CancellationToken ct = default)
    {
        if (ApiTokenService.IsApiToken(token)) return null;
        var session = await db.AuthSessions.FirstOrDefaultAsync(a => a.Token == token, ct);
        if (session is null) return null;

        if (StoredTime.Utc(session.ExpiresAt) <= DateTime.UtcNow)
        {
            db.AuthSessions.Remove(session);
            await db.SaveChangesAsync(ct);
            return null;
        }
        return session.UserId;
    }

    public async Task LogoutAsync(string token, CancellationToken ct = default)
    {
        if (ApiTokenService.IsApiToken(token))
        {
            await _apiTokens.RevokeBearerAsync(token, ct);
            return;
        }
        var session = await db.AuthSessions.FirstOrDefaultAsync(a => a.Token == token, ct);
        if (session is not null)
        {
            db.AuthSessions.Remove(session);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<string> CreateSessionAsync(string userId, CancellationToken ct)
    {
        var token = GenerateToken();
        db.AuthSessions.Add(new AuthSession
        {
            Token = token,
            UserId = userId,
            ExpiresAt = DateTime.UtcNow + SessionTtl,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    private static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
