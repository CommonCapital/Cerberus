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
    public List<Job> Jobs { get; set; } = [];
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

    /// <summary>LLM triage opinions on findings (list of TriageResult), as JSON. Advisory only.</summary>
    public string Triage { get; set; } = "[]";
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

    /// <summary>A model's suggested answers (list of ControlSuggestion), serialized as JSON. Advisory: they change nothing until the operator accepts one.</summary>
    public string Suggestions { get; set; } = "[]";
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

    /// <summary>Random token the operator must publish to prove control (live targets only).</summary>
    public string? ChallengeToken { get; set; }

    /// <summary>How control was proven: Dns | Http.</summary>
    public string? VerificationMethod { get; set; }
    public DateTime? VerifiedAt { get; set; }
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

/// <summary>
/// A unit of background work (PLAN.md §12). State moves forward only:
/// Queued → Running → Succeeded | Failed | Cancelled | TimedOut.
/// </summary>
public class Job
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }
    public string? EngagementId { get; set; }

    /// <summary>What to run. Today: "scan".</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Capability tier of the work (T0..T3).</summary>
    public string Tier { get; set; } = "T0";
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>Type-specific options, as JSON.</summary>
    public string Parameters { get; set; } = "{}";
    public string State { get; set; } = "Queued";
    public string? Error { get; set; }

    /// <summary>Id of what the job produced (for a scan job, the Scan).</summary>
    public string? ResultId { get; set; }

    /// <summary>The approval request this job had to wait for, if it needed one.</summary>
    public string? ApprovalId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>
/// A request for the operator's consent before a job may run (human in the
/// loop, PLAN.md §6.5). It names one specific action; approving it approves
/// that job and nothing else.
/// </summary>
public class ApprovalRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string? EngagementId { get; set; }

    /// <summary>Machine-readable reason, e.g. "send-code-to-cloud-model".</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Every action kind this request covers (JSON list); what "always allow" would remember.</summary>
    public string Kinds { get; set; } = "[]";

    /// <summary>One line: what the operator is being asked to allow.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The specifics: what will happen, to what, and what leaves the machine.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Pending | Approved | Denied | Cancelled.</summary>
    public string Status { get; set; } = "Pending";

    /// <summary>The operator's optional comment on their decision.</summary>
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DecidedAt { get; set; }
}

/// <summary>
/// One event on a run's stream (PLAN.md §6.5): a state change, a step, progress,
/// a finding, model text. Ordered per job by <see cref="Sequence"/>, so a
/// client can replay a run and continue live from where it left off.
/// </summary>
public class JobEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string JobId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    /// <summary>1-based position in the job's stream.</summary>
    public long Sequence { get; set; }
    public string Type { get; set; } = string.Empty;

    /// <summary>The event's payload, as JSON.</summary>
    public string Data { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A question a running job put to the operator (human in the loop, PLAN.md
/// §6.5). The job waits in AwaitingInput until it is answered, expires, or the
/// job is cancelled.
/// </summary>
public class OperatorQuestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public string JobId { get; set; } = string.Empty;
    public string? EngagementId { get; set; }
    public string Text { get; set; } = string.Empty;

    /// <summary>The choices offered: list of { label, description }, as JSON.</summary>
    public string Options { get; set; } = "[]";
    public bool MultiSelect { get; set; }
    public bool AllowText { get; set; }

    /// <summary>Pending | Answered | Expired | Cancelled.</summary>
    public string Status { get; set; } = "Pending";

    /// <summary>The operator's answer: { selected, text }, as JSON.</summary>
    public string? Answer { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
}

/// <summary>
/// A permission policy (PLAN.md §6.5): the user's default (EngagementId null)
/// or one engagement's override.
/// </summary>
public class PermissionSetting
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public string? EngagementId { get; set; }

    /// <summary>Default | Ask.</summary>
    public string Mode { get; set; } = "Default";

    /// <summary>Action kinds that never ask (JSON list).</summary>
    public string AllowedKinds { get; set; } = "[]";

    /// <summary>Shell commands the chat agent may run without asking, as command prefixes such as "git status" (JSON list).</summary>
    public string AllowedCommands { get; set; } = "[]";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The outcome of a review: the scan and assessments it produced, and its
/// report (Markdown).
/// </summary>
public class Review
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public string? EngagementId { get; set; }
    public string JobId { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string ScanId { get; set; } = string.Empty;

    /// <summary>The SAST agent's run, when the review included one.</summary>
    public string? AgentRunId { get; set; }

    /// <summary>The threat-model agent's run, when the review included one.</summary>
    public string? ThreatModelRunId { get; set; }

    /// <summary>Ids of the compliance assessments (JSON list).</summary>
    public string AssessmentIds { get; set; } = "[]";
    public int FindingCount { get; set; }
    public string Report { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// What a model-driven agent produced in one job: its closing summary and
/// the findings it reported (JSON). Kept apart from scans — a model's
/// findings are candidates and never feed a compliance verdict.
/// </summary>
public class AgentRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public string? EngagementId { get; set; }
    public string JobId { get; set; } = string.Empty;
    public string Agent { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Findings { get; set; } = "[]";
    public int FindingCount { get; set; }
    public int Steps { get; set; }
    public int ToolCalls { get; set; }

    /// <summary>False when the run was stopped early: cancelled, failed, timed out or at its step limit.</summary>
    public bool Complete { get; set; }

    /// <summary>A written report of the run (Markdown), for agents that produce one. Empty otherwise.</summary>
    public string Report { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Something remembered across runs: a standing instruction from the
/// operator, or a note (by the operator, or by an agent during a run).
/// </summary>
public class MemoryNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;

    /// <summary>Null: applies to all the user's work. Set: only under that engagement.</summary>
    public string? EngagementId { get; set; }

    /// <summary>instruction | note.</summary>
    public string Kind { get; set; } = "note";

    /// <summary>operator | agent.</summary>
    public string Source { get; set; } = "operator";

    /// <summary>For a note about one target: its normalized path. Null: about no target in particular.</summary>
    public string? TargetPath { get; set; }
    public string Text { get; set; } = string.Empty;

    /// <summary>The job whose agent wrote it, for agent notes.</summary>
    public string? JobId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The operator's decision on a finding: accepted risk or false positive, with
/// the reason. Keyed by the finding's fingerprint, so it applies to that
/// finding in every scan.
/// </summary>
public class FindingDecision
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>accepted-risk | false-positive.</summary>
    public string Status { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    // What it was decided on, for display where no scan is at hand.
    public string RuleId { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A semantic index of one target: its code cut into chunks, each with a
/// vector from an embedding model. Only locations and vectors are stored —
/// the code itself stays in its files.
/// </summary>
public class CodeIndex
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");
    public string UserId { get; set; } = string.Empty;
    public string? EngagementId { get; set; }

    /// <summary>Normalized path of the target under the scan root; empty for the scan root itself.</summary>
    public string TargetPath { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public int Dimensions { get; set; }
    public int FileCount { get; set; }
    public int ChunkCount { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class CodeChunk
{
    public long Id { get; set; }
    public string IndexId { get; set; } = string.Empty;

    /// <summary>Relative to the index's target.</summary>
    public string File { get; set; } = string.Empty;

    /// <summary>Hash of the file's content when it was indexed: tells a changed file from an unchanged one.</summary>
    public string FileHash { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>The vector, normalized, as little-endian floats.</summary>
    public byte[] Embedding { get; set; } = [];
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

    /// <summary>A summary standing in, for the model, for the messages up to <see cref="SummarizedThrough"/>. Null until the chat was compacted.</summary>
    public string? Summary { get; set; }

    /// <summary>Id of the last message the summary covers.</summary>
    public string? SummarizedThrough { get; set; }

    /// <summary>The chat agent's task list (JSON list of tasks), as it last set it. Null until it made one.</summary>
    public string? Tasks { get; set; }
}
