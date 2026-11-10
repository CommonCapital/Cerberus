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
    public List<Engagement> Engagements { get; set; } = [];
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

    /// <summary>The engagement this run was authorized under, if any.</summary>
    public string? EngagementId { get; set; }
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

    /// <summary>The engagement this run was authorized under, if any.</summary>
    public string? EngagementId { get; set; }

    public int TotalControls { get; set; }
    public int Compliant { get; set; }
    public int NonCompliant { get; set; }
    public int NotAssessed { get; set; }
    public double? Score { get; set; }

    /// <summary>The full control matrix (list of ControlResult), serialized as JSON.</summary>
    public string Matrix { get; set; } = "[]";

    /// <summary>Operator answers (list of OperatorResponse), serialized as JSON. They overrule the automated matrix.</summary>
    public string Responses { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An authorized piece of work (PLAN.md §3): who may test which targets, up to
/// which capability tier, and when. Enum-like fields are stored as their names.
/// </summary>
public class Engagement
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Active | Closed | Aborted.</summary>
    public string Status { get; set; } = "Active";

    /// <summary>Highest capability tier allowed: T0..T3.</summary>
    public string MaxTier { get; set; } = "T0";
    public DateTime? StartsAt { get; set; }
    public DateTime? EndsAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the engagement was closed or aborted.</summary>
    public DateTime? EndedAt { get; set; }

    public List<EngagementTarget> Targets { get; set; } = [];
}

/// <summary>A target that is in scope for an engagement.</summary>
public class EngagementTarget
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string EngagementId { get; set; } = string.Empty;
    public Engagement? Engagement { get; set; }

    /// <summary>Path | Host | Domain.</summary>
    public string Type { get; set; } = "Path";

    /// <summary>Normalized locator (path relative to the scan root, or host name).</summary>
    public string Locator { get; set; } = string.Empty;

    /// <summary>Pending | Attested | Verified.</summary>
    public string Verification { get; set; } = "Pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One entry of the append-only audit log. Rows are never updated or deleted
/// (enforced by database triggers) and are hash-chained per user. Deliberately
/// has no foreign keys, so removing a user or engagement cannot remove history.
/// </summary>
public class AuditEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;

    /// <summary>1-based position in this user's chain.</summary>
    public long Sequence { get; set; }
    public string? EngagementId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Target { get; set; }
    public string? Tier { get; set; }

    /// <summary>allowed | denied | ok | error.</summary>
    public string Outcome { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string PreviousHash { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
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
