using Microsoft.EntityFrameworkCore;

namespace Cerberus.Data;

public class CerberusDbContext(DbContextOptions<CerberusDbContext> options) : DbContext(options)
{
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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

        modelBuilder.Entity<JobEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.JobId, x.Sequence }).IsUnique();
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
