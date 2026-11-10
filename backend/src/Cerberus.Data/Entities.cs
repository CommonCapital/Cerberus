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
    public List<ComplianceAssessment> ComplianceAssessments { get; set; } = [];
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

    /// <summary>True when the dependency (SCA) check ran and covered every discovered dependency.</summary>
    public bool ScaCompleted { get; set; }
    public string Findings { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A persisted Phase-2 compliance assessment (control matrix stored as JSON).</summary>
public class ComplianceAssessment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }
    public string CatalogId { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>The scan whose findings drove this assessment, when one was reused.</summary>
    public string? ScanId { get; set; }

    public int TotalControls { get; set; }
    public int Compliant { get; set; }
    public int NonCompliant { get; set; }
    public int NotAssessed { get; set; }
    public double? Score { get; set; }

    /// <summary>The full control matrix (list of ControlResult), serialized as JSON.</summary>
    public string Matrix { get; set; } = "[]";
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
