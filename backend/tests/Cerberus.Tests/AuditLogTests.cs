using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api.Audit;
using Cerberus.Authorization;
using Cerberus.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cerberus.Tests;

public class AuditLogTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    public async Task New_entries_are_hashed_at_the_precision_Postgres_can_store(int extraTicks)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var services = new ServiceCollection()
            .AddDbContext<CerberusDbContext>(options => options.UseSqlite(connection))
            .BuildServiceProvider();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        await db.Database.EnsureCreatedAsync();
        var precise = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_560 + extraTicks);
        var log = new AuditLog(services.GetRequiredService<IServiceScopeFactory>(), new Clock(precise));

        await log.AppendAsync("owner", "job.submit", AuditOutcome.Allowed, "In scope");
        await log.AppendAsync("owner", "engagement.abort", AuditOutcome.Ok, "Stopped");

        var stored = await db.AuditEvents.AsNoTracking().OrderBy(e => e.Sequence).ToListAsync();
        Assert.Equal(2, stored.Count);
        foreach (var entry in stored)
        {
            Assert.Equal(precise.Ticks - extraTicks, entry.CreatedAt.Ticks);
            // Simulate the provider's timestamp representation after a
            // round-trip; it must no longer change any hashed field.
            var roundTripped = AuditLog.ToChained(entry) with
            {
                Record = AuditLog.ToChained(entry).Record with
                {
                    CreatedAt = entry.CreatedAt.AddTicks(-(entry.CreatedAt.Ticks % 10)),
                },
            };
            Assert.Equal(entry.Hash, AuditChain.ComputeHash(roundTripped.Record, entry.PreviousHash));
        }
        Assert.Equal(new ChainVerification(true, 2, null, null), AuditChain.Verify(stored.Select(AuditLog.ToChained)));
    }

    [Fact]
    public async Task Appending_preserves_historical_SQLite_ticks_and_hashes_and_still_detects_single_tick_tampering()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var services = new ServiceCollection()
            .AddDbContext<CerberusDbContext>(options => options.UseSqlite(connection))
            .BuildServiceProvider();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        SchemaUpgrader.EnsureUpToDate(db); // Includes the append-only database triggers.
        var at = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc).AddTicks(1_234_567);
        var historical = new AuditRecord(1, "owner", null, "scan.run", null, null, "ok", "Original", at);
        var historicalHash = AuditChain.ComputeHash(historical, AuditChain.Genesis);
        db.AuditEvents.Add(new AuditEvent
        {
            UserId = historical.UserId,
            Sequence = historical.Sequence,
            Action = historical.Action,
            Outcome = historical.Outcome,
            Detail = historical.Detail,
            CreatedAt = historical.CreatedAt,
            PreviousHash = AuditChain.Genesis,
            Hash = historicalHash,
        });
        await db.SaveChangesAsync();
        var log = new AuditLog(services.GetRequiredService<IServiceScopeFactory>(), new Clock(new DateTimeOffset(at.AddSeconds(1))));

        await log.AppendAsync("owner", "scan.run", AuditOutcome.Ok, "New");

        var stored = await db.AuditEvents.AsNoTracking().OrderBy(e => e.Sequence).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal(at.Ticks, stored[0].CreatedAt.Ticks);
        Assert.Equal(historicalHash, stored[0].Hash);
        Assert.Equal(historicalHash, stored[1].PreviousHash);
        Assert.True(AuditChain.Verify(stored.Select(AuditLog.ToChained)).Intact);
        var changed = stored.Select(AuditLog.ToChained).ToArray();
        changed[0] = changed[0] with { Record = changed[0].Record with { CreatedAt = at.AddTicks(1) } };
        Assert.Equal(1L, AuditChain.Verify(changed).FirstBrokenSequence);
    }
}
[Collection("api")]
public class AuditLogApiTests(ApiHost host)
{
    [Fact]
    public async Task Audit_hash_covers_the_text_the_database_actually_stores()
    {
        var (client, _) = await host.NewUserAsync();
        using var owner = client;
        const string name = "Audit\0regression";
        using var response = await owner.PostAsJsonAsync("/engagements", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var engagementId = created.GetProperty("id").GetString();

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        var stored = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.EngagementId == engagementId && e.Action == "engagement.create");
        var expectedName = db.IsPostgres ? name.Replace('\0', PostgresStorage.NulStandIn) : name;
        Assert.Equal($"Created engagement \"{expectedName}\" (max tier T0).", stored.Detail);
        Assert.Equal(stored.Hash, AuditChain.ComputeHash(AuditLog.ToChained(stored).Record, stored.PreviousHash));
        var verification = await owner.GetFromJsonAsync<JsonElement>("/audit/verify");
        Assert.True(verification.GetProperty("intact").GetBoolean());
    }
}
