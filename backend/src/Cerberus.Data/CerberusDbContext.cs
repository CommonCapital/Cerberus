using Microsoft.EntityFrameworkCore;

namespace Cerberus.Data;

public class CerberusDbContext(DbContextOptions<CerberusDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();
    public DbSet<Session> Sessions => Set<Session>();

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
    }
}
