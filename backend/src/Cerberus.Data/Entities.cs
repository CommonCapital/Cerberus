namespace Cerberus.Data;

public class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<AuthSession> AuthSessions { get; set; } = [];
    public List<Session> Sessions { get; set; } = [];
    public List<UserApiKey> ApiKeys { get; set; } = [];
    public List<Scan> Scans { get; set; } = [];
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

/// <summary>A user's API key for a cloud model provider, encrypted at rest.</summary>
public class UserApiKey
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }
    public string Provider { get; set; } = string.Empty; // "anthropic" | "openai"
    public string EncryptedKey { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A persisted scan run and its findings (stored as a JSON string).</summary>
public class Scan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public int FilesScanned { get; set; }
    public int FindingCount { get; set; }
    public string Findings { get; set; } = "[]";
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
