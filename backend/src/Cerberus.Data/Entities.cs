namespace Cerberus.Data;

public class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<AuthSession> AuthSessions { get; set; } = [];
    public List<Session> Sessions { get; set; } = [];
}

/// <summary>A local authentication session (replaces Clerk). The CLI stores the token.</summary>
public class AuthSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Token { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A chat/work session. Messages are stored as a JSON string.</summary>
public class Session
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string Messages { get; set; } = "[]";
}
