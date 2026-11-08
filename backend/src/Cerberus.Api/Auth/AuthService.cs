using System.Security.Cryptography;
using Cerberus.Core.Security;
using Cerberus.Data;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Auth;

/// <summary>Local, self-hosted authentication (replaces Clerk).</summary>
public sealed class AuthService(CerberusDbContext db)
{
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(30);

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

    public async Task<string?> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        var session = await db.AuthSessions.FirstOrDefaultAsync(a => a.Token == token, ct);
        if (session is null) return null;

        if (session.ExpiresAt < DateTime.UtcNow)
        {
            db.AuthSessions.Remove(session);
            await db.SaveChangesAsync(ct);
            return null;
        }
        return session.UserId;
    }

    public async Task LogoutAsync(string token, CancellationToken ct = default)
    {
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
