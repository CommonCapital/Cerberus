using Microsoft.EntityFrameworkCore;

namespace Cerberus.Data;

/// <param name="owner">
/// Whose rows this context may see (<see cref="DataOwner"/>). Null — a context made without one,
/// as some tests do — is unrestricted.
/// </param>
public class CerberusDbContext(DbContextOptions<CerberusDbContext> options, DataOwner? owner = null) : DbContext(options)
{
    // Read by the query filters below each time a query runs, so a context restricted
    // after it was created (sign-in is checked inside the request) is restricted from then on.
    private bool OwnerRestricted => owner?.Restricted ?? false;
    private string? OwnerId => owner?.UserId;

    public DbSet<User> Users => Set<User>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<UserApiKey> UserApiKeys => Set<UserApiKey>();
    public DbSet<Scan> Scans => Set<Scan>();
    public DbSet<ComplianceAssessment> ComplianceAssessments => Set<ComplianceAssessment>();
    public DbSet<Engagement> Engagements => Set<Engagement>();
    public DbSet<EngagementTarget> EngagementTargets => Set<EngagementTarget>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<JobEvent> JobEvents => Set<JobEvent>();
    public DbSet<ChatTurn> ChatTurns => Set<ChatTurn>();
    public DbSet<ChatTurnEvent> ChatTurnEvents => Set<ChatTurnEvent>();
    public DbSet<PermissionSetting> PermissionSettings => Set<PermissionSetting>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<MemoryNote> MemoryNotes => Set<MemoryNote>();
    public DbSet<FindingDecision> FindingDecisions => Set<FindingDecision>();
    public DbSet<CodeIndex> CodeIndexes => Set<CodeIndex>();
    public DbSet<CodeChunk> CodeChunks => Set<CodeChunk>();
    public DbSet<OperatorQuestion> OperatorQuestions => Set<OperatorQuestion>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<RunArtifact> RunArtifacts => Set<RunArtifact>();
    public DbSet<WorkerInstance> WorkerInstances => Set<WorkerInstance>();

    /// <summary>The second lock: a restricted context sees one user's rows of this type, whatever the query says.</summary>
    private void OwnedByUser<T>(ModelBuilder modelBuilder) where T : class, IUserOwned =>
        modelBuilder.Entity<T>().HasQueryFilter(e => !OwnerRestricted || e.UserId == OwnerId);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RefuseForeignWrites();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RefuseForeignWrites();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// A restricted context does not write another user's rows either: a row
    /// added, changed or removed must belong to the user it is restricted to.
    /// </summary>
    private void RefuseForeignWrites()
    {
        if (!OwnerRestricted) return;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity is IUserOwned row && !string.Equals(row.UserId, OwnerId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"A {entry.Metadata.ClrType.Name} belonging to another user cannot be written through a context restricted to one user.");
            }
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Every type with an owner. A sign-in session is not among them: it is looked up by its
        // secret token before the user is known. Users have no owner but themselves.
        OwnedByUser<UserApiKey>(modelBuilder);
        OwnedByUser<Scan>(modelBuilder);
        OwnedByUser<ComplianceAssessment>(modelBuilder);
        OwnedByUser<Engagement>(modelBuilder);
        OwnedByUser<AuditEvent>(modelBuilder);
        OwnedByUser<Job>(modelBuilder);
        OwnedByUser<ApprovalRequest>(modelBuilder);
        OwnedByUser<ChatTurn>(modelBuilder);
        OwnedByUser<ChatTurnEvent>(modelBuilder);
        OwnedByUser<JobEvent>(modelBuilder);
        OwnedByUser<OperatorQuestion>(modelBuilder);
        OwnedByUser<PermissionSetting>(modelBuilder);
        OwnedByUser<Review>(modelBuilder);
        OwnedByUser<AgentRun>(modelBuilder);
        OwnedByUser<MemoryNote>(modelBuilder);
        OwnedByUser<FindingDecision>(modelBuilder);
        OwnedByUser<CodeIndex>(modelBuilder);
        OwnedByUser<Session>(modelBuilder);
        OwnedByUser<Schedule>(modelBuilder);
        OwnedByUser<RunArtifact>(modelBuilder);
        // Owned through their parent.
        modelBuilder.Entity<EngagementTarget>().HasQueryFilter(t => !OwnerRestricted || t.Engagement!.UserId == OwnerId);
        modelBuilder.Entity<CodeChunk>().HasQueryFilter(c => !OwnerRestricted || CodeIndexes.Any(i => i.Id == c.IndexId && i.UserId == OwnerId));

        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Email).IsUnique();
            e.HasMany(u => u.AuthSessions).WithOne(a => a.User!).HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(u => u.Sessions).WithOne(s => s.User!).HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuthSession>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.Token).IsUnique();
            e.HasIndex(a => a.UserId);
        });

        modelBuilder.Entity<Session>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.UserId);
        });

        modelBuilder.Entity<UserApiKey>(e =>
        {
            e.HasKey(k => k.Id);
            e.HasIndex(k => new { k.UserId, k.Provider }).IsUnique();
            e.HasOne(k => k.User).WithMany(u => u.ApiKeys).HasForeignKey(k => k.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Scan>(e =>
        {
            e.HasKey(s => s.Id);
            e.HasIndex(s => s.UserId);
            e.HasOne(s => s.User).WithMany(u => u.Scans).HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Engagement>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany(u => u.Engagements).HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Targets).WithOne(t => t.Engagement!).HasForeignKey(t => t.EngagementId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EngagementTarget>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => new { t.EngagementId, t.Type, t.Locator }).IsUnique();
        });

        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.HasKey(a => a.Id);
            // One chain per user; uniqueness also stops two writers claiming the same position.
            e.HasIndex(a => new { a.UserId, a.Sequence }).IsUnique();
            e.HasIndex(a => a.EngagementId);
        });

        modelBuilder.Entity<Review>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.UserId, r.CreatedAt });
        });

        modelBuilder.Entity<AgentRun>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.UserId, r.CreatedAt });
        });

        modelBuilder.Entity<MemoryNote>(e =>
        {
            e.HasKey(m => m.Id);
            e.HasIndex(m => new { m.UserId, m.CreatedAt });
        });

        modelBuilder.Entity<FindingDecision>(e =>
        {
            e.HasKey(d => d.Id);
            e.HasIndex(d => new { d.UserId, d.Fingerprint }).IsUnique();
        });

        modelBuilder.Entity<CodeIndex>(e =>
        {
            e.HasKey(i => i.Id);
            e.HasIndex(i => new { i.UserId, i.TargetPath, i.Model }).IsUnique();
        });

        modelBuilder.Entity<CodeChunk>(e =>
        {
            e.HasKey(c => c.Id);
            e.HasIndex(c => c.IndexId);
        });

        modelBuilder.Entity<PermissionSetting>(e =>
        {
            e.HasKey(p => p.Id);
            e.HasIndex(p => new { p.UserId, p.EngagementId }).IsUnique();
        });

        modelBuilder.Entity<OperatorQuestion>(e =>
        {
            e.HasKey(q => q.Id);
            e.HasIndex(q => new { q.UserId, q.Status });
            e.HasIndex(q => q.JobId);
        });

        modelBuilder.Entity<JobEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.JobId, x.Sequence }).IsUnique();
        });

        modelBuilder.Entity<ChatTurn>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.State });
            e.HasIndex(x => new { x.SessionId, x.CreatedAt });
        });

        modelBuilder.Entity<WorkerInstance>(e => e.HasKey(x => x.Id));

        modelBuilder.Entity<ChatTurnEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TurnId, x.Sequence }).IsUnique();
        });

        modelBuilder.Entity<ApprovalRequest>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.UserId, a.Status });
            e.HasIndex(a => a.JobId);
        });

        modelBuilder.Entity<Job>(e =>
        {
            e.HasKey(j => j.Id);
            e.HasIndex(j => new { j.UserId, j.CreatedAt });
            e.HasIndex(j => j.State);
            e.HasIndex(j => j.EngagementId);
            e.HasOne(j => j.User).WithMany(u => u.Jobs).HasForeignKey(j => j.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ComplianceAssessment>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => a.UserId);
            e.HasOne(a => a.User).WithMany(u => u.ComplianceAssessments).HasForeignKey(a => a.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
