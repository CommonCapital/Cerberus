using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cerberus.Api.Auth;
using Cerberus.Data;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cerberus.Tests;

[Collection("api")]
public class ApiTokenTests(ApiHost host)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private HttpClient Bearer(string token)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, string name = "Build pipeline", int? days = null)
    {
        var body = new Dictionary<string, object?> { ["name"] = name };
        if (days is not null) body["expiresInDays"] = days.Value;
        using var response = await client.PostAsJsonAsync("/auth/tokens", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<ApiToken> StoredAsync(string id)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        return await db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    private async Task ChangeStoredAsync(string id, Action<ApiToken> change)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        change(await db.ApiTokens.SingleAsync(t => t.Id == id));
        await db.SaveChangesAsync();
    }

    private static void Metadata(JsonElement value, string id, string name, string prefix)
    {
        Assert.Equal(id, value.GetProperty("id").GetString());
        Assert.Equal(name, value.GetProperty("name").GetString());
        Assert.Equal(prefix, value.GetProperty("prefix").GetString());
        Assert.True(value.GetProperty("expiresAt").GetDateTime() > value.GetProperty("createdAt").GetDateTime());
        Assert.True(value.TryGetProperty("revokedAt", out _));
        Assert.False(value.TryGetProperty("tokenHash", out _));
        Assert.False(value.TryGetProperty("hash", out _));
        Assert.False(value.TryGetProperty("userId", out _));
    }

    private static async Task UnauthorizedEverywhereAsync(HttpClient client, string id)
    {
        using var read = await client.GetAsync("/sessions");
        using var list = await client.GetAsync("/auth/tokens");
        using var create = await client.PostAsJsonAsync("/auth/tokens", new { name = "Must not be created" });
        using var revoke = await client.DeleteAsync($"/auth/tokens/{id}");
        foreach (var response in new[] { read, list, create, revoke })
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Creation_returns_the_secret_once_and_only_its_hash_is_stored()
    {
        var (client, loginToken) = await host.NewUserAsync();
        using var owner = client;
        var created = await CreateAsync(owner);
        var id = created.GetProperty("id").GetString()!;
        var token = created.GetProperty("token").GetString()!;
        var prefix = created.GetProperty("prefix").GetString()!;
        Metadata(created, id, "Build pipeline", prefix);
        Assert.Matches("^cerb_[A-Za-z0-9_-]{43}$", token);
        Assert.Equal(token[..12], prefix);
        Assert.Equal(JsonValueKind.Null, created.GetProperty("revokedAt").ValueKind);
        Assert.InRange((created.GetProperty("expiresAt").GetDateTime() - created.GetProperty("createdAt").GetDateTime()).TotalSeconds,
            TimeSpan.FromDays(90).TotalSeconds - 1, TimeSpan.FromDays(90).TotalSeconds + 1);

        var listed = await owner.GetFromJsonAsync<JsonElement>("/auth/tokens");
        var metadata = Assert.Single(listed.EnumerateArray());
        Metadata(metadata, id, "Build pipeline", prefix);
        Assert.False(metadata.TryGetProperty("token", out _));
        Assert.DoesNotContain(token, listed.GetRawText());

        var stored = await StoredAsync(id);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        Assert.Equal(hash, stored.TokenHash);
        Assert.Equal(prefix, stored.TokenPrefix);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(stored));
        Assert.DoesNotContain(hash, listed.GetRawText());
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        Assert.False(await db.AuthSessions.AnyAsync(s => s.Token == token));
        Assert.Equal((await db.AuthSessions.SingleAsync(s => s.Token == loginToken)).UserId, stored.UserId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(365)]
    public async Task Expiry_accepts_both_supported_day_boundaries(int days)
    {
        var (client, _) = await host.NewUserAsync();
        using var owner = client;
        var created = await CreateAsync(owner, new string('x', 100), days);
        var elapsed = created.GetProperty("expiresAt").GetDateTime() - created.GetProperty("createdAt").GetDateTime();
        Assert.InRange(elapsed.TotalSeconds, TimeSpan.FromDays(days).TotalSeconds - 1, TimeSpan.FromDays(days).TotalSeconds + 1);
        using var api = Bearer(created.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/sessions")).StatusCode);
    }

    public static IEnumerable<object?[]> InvalidNames =>
    [
        [null], [""], ["   "], [new string('x', 101)], ["build\npipeline"], ["build\0pipeline"], ["build\u007fpipeline"],
        ["pipeline\n"], ["\tpipeline"],
    ];

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public async Task Invalid_names_are_rejected_without_creating_a_token(string? name)
    {
        var (client, _) = await host.NewUserAsync();
        using var owner = client;
        using var response = await owner.PostAsJsonAsync("/auth/tokens", new { name });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>("/auth/tokens")).EnumerateArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(366)]
    [InlineData(int.MaxValue)]
    public async Task Unsupported_expiry_is_rejected_without_creating_a_token(int days)
    {
        var (client, _) = await host.NewUserAsync();
        using var owner = client;
        using var response = await owner.PostAsJsonAsync("/auth/tokens", new { name = "CI", expiresInDays = days });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<JsonElement>("/auth/tokens")).EnumerateArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-real-token")]
    public async Task Management_rejects_missing_or_invalid_auth_before_applying_permissions(string? token)
    {
        using var client = token is null ? host.CreateClient() : Bearer(token);
        await UnauthorizedEverywhereAsync(client, "unknown");
    }

    [Fact]
    public async Task An_api_token_can_read_its_owners_sessions_but_cannot_manage_any_tokens()
    {
        var (client, _) = await host.NewUserAsync();
        using var owner = client;
        var (otherClient, _) = await host.NewUserAsync();
        using var other = otherClient;
        var own = await CreateAsync(owner);
        var theirs = await CreateAsync(other);
        using var api = Bearer(own.GetProperty("token").GetString()!);
        using var otherApi = Bearer(theirs.GetProperty("token").GetString()!);

        using var saved = await owner.PostAsJsonAsync("/sessions", new { title = "Owner's session" });
        saved.EnsureSuccessStatusCode();
        var session = await saved.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("id").GetString()!;
        var read = await api.GetFromJsonAsync<JsonElement>($"/sessions/{sessionId}");
        Assert.Equal(session.GetProperty("userId").GetString(), read.GetProperty("userId").GetString());
        Assert.Contains((await api.GetFromJsonAsync<JsonElement>("/sessions")).EnumerateArray(), s => s.GetProperty("id").GetString() == sessionId);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/sessions/{sessionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherApi.GetAsync($"/sessions/{sessionId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync("/auth/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsJsonAsync("/auth/tokens", new { name = "Escalation" })).StatusCode);
        foreach (var id in new[] { own.GetProperty("id").GetString(), theirs.GetProperty("id").GetString(), "unknown" })
            Assert.Equal(HttpStatusCode.Forbidden, (await api.DeleteAsync($"/auth/tokens/{id}")).StatusCode);
        Assert.Null((await StoredAsync(own.GetProperty("id").GetString()!)).RevokedAt);
        Assert.Null((await StoredAsync(theirs.GetProperty("id").GetString()!)).RevokedAt);
        Assert.Single((await owner.GetFromJsonAsync<JsonElement>("/auth/tokens")).EnumerateArray());
    }

    [Fact]
    public async Task Listing_and_revocation_are_isolated_between_interactive_users()
    {
        var (client, _) = await host.NewUserAsync();
        using var alice = client;
        var (otherClient, _) = await host.NewUserAsync();
        using var bob = otherClient;
        var own = await CreateAsync(alice, "Alice CI");
        var theirs = await CreateAsync(bob, "Bob CI");
        var ownId = own.GetProperty("id").GetString()!;
        var otherId = theirs.GetProperty("id").GetString()!;
        Assert.Equal(ownId, Assert.Single((await alice.GetFromJsonAsync<JsonElement>("/auth/tokens")).EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(otherId, Assert.Single((await bob.GetFromJsonAsync<JsonElement>("/auth/tokens")).EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync($"/auth/tokens/{otherId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/auth/tokens/{ownId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.DeleteAsync("/auth/tokens/unknown")).StatusCode);
        Assert.Null((await StoredAsync(otherId)).RevokedAt);
        using var api = Bearer(theirs.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/sessions")).StatusCode);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        scope.ServiceProvider.GetRequiredService<DataOwner>().RestrictTo((await StoredAsync(ownId)).UserId);
        Assert.Equal(ownId, Assert.Single(await db.ApiTokens.AsNoTracking().ToListAsync()).Id);
    }

    [Fact]
    public async Task Revocation_is_idempotent_and_keeps_login_other_tokens_and_accepted_jobs_valid()
    {
        var (client, _) = await host.NewUserAsync();
        using var owner = client;
        var created = await CreateAsync(owner);
        var second = await CreateAsync(owner, "Another pipeline");
        var id = created.GetProperty("id").GetString()!;
        using var api = Bearer(created.GetProperty("token").GetString()!);
        using var otherApi = Bearer(second.GetProperty("token").GetString()!);
        using var submitted = await api.PostAsJsonAsync("/jobs", new { type = "scan", path = "app", sca = false });
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var jobId = (await submitted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/auth/tokens/{id}")).StatusCode);
        var revokedAt = (await StoredAsync(id)).RevokedAt;
        Assert.NotNull(revokedAt);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/auth/tokens/{id}")).StatusCode);
        Assert.Equal(revokedAt, (await StoredAsync(id)).RevokedAt);
        await UnauthorizedEverywhereAsync(api, id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync($"/jobs/{jobId}/events?after=0")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherApi.GetAsync("/sessions")).StatusCode);
        var listed = await owner.GetFromJsonAsync<JsonElement>("/auth/tokens");
        Assert.Equal(revokedAt, Assert.Single(listed.EnumerateArray(), t => t.GetProperty("id").GetString() == id).GetProperty("revokedAt").GetDateTime());

        var deadline = DateTime.UtcNow + Patience;
        JsonElement job;
        do
        {
            job = await owner.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}");
            if (job.GetProperty("state").GetString() is not ("Queued" or "Running")) break;
            Assert.True(DateTime.UtcNow < deadline, "The accepted scan did not finish.");
            await Task.Delay(25);
        } while (true);
        Assert.Equal("Succeeded", job.GetProperty("state").GetString());
        var replay = await otherApi.GetFromJsonAsync<JsonElement>($"/jobs/{jobId}/events?after=0");
        Assert.NotEmpty(replay.GetProperty("events").EnumerateArray());
        var scan = await otherApi.GetFromJsonAsync<JsonElement>($"/scans/{job.GetProperty("resultId").GetString()}");
        Assert.True(scan.GetProperty("findingCount").GetInt32() > 0);
        var (strangerClient, _) = await host.NewUserAsync();
        using var stranger = strangerClient;
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/jobs/{jobId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/jobs/{jobId}/events?after=0")).StatusCode);
    }

    [Fact]
    public async Task Logging_out_an_api_token_revokes_only_that_token()
    {
        var (client, _) = await host.NewUserAsync();
        using var owner = client;
        var first = await CreateAsync(owner);
        var second = await CreateAsync(owner, "Still valid");
        using var api = Bearer(first.GetProperty("token").GetString()!);
        using var other = Bearer(second.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/auth/logout", null)).StatusCode);
        Assert.NotNull((await StoredAsync(first.GetProperty("id").GetString()!)).RevokedAt);
        await UnauthorizedEverywhereAsync(api, first.GetProperty("id").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/auth/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/sessions")).StatusCode);
    }

    [Fact]
    public async Task Logging_out_an_interactive_session_does_not_revoke_its_api_token()
    {
        var (client, _) = await host.NewUserAsync();
        using var owner = client;
        var created = await CreateAsync(owner);
        using var api = Bearer(created.GetProperty("token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsync("/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/sessions")).StatusCode);
        Assert.Null((await StoredAsync(created.GetProperty("id").GetString()!)).RevokedAt);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("wrong-secret")]
    [InlineData("wrong-hash")]
    public async Task Invalid_api_credentials_are_unauthorized_including_management(string reason)
    {
        var (client, _) = await host.NewUserAsync();
        using var owner = client;
        var created = await CreateAsync(owner);
        var id = created.GetProperty("id").GetString()!;
        var token = created.GetProperty("token").GetString()!;
        switch (reason)
        {
            case "expired": await ChangeStoredAsync(id, t => t.ExpiresAt = DateTime.UtcNow.AddTicks(-1)); break;
            case "revoked": await ChangeStoredAsync(id, t => t.RevokedAt = DateTime.UtcNow); break;
            case "wrong-secret": token = token[..^1] + (token[^1] == 'A' ? 'B' : 'A'); break;
            case "wrong-hash": await ChangeStoredAsync(id, t => t.TokenHash = new string('0', 64)); break;
        }
        using var api = Bearer(token);
        await UnauthorizedEverywhereAsync(api, id);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/auth/tokens")).StatusCode);
    }

    [Theory]
    [InlineData("/hub/jobs")]
    [InlineData("/hub/chat")]
    public async Task Rest_only_api_tokens_cannot_open_a_persistent_hub(string path)
    {
        var (client, loginToken) = await host.NewUserAsync();
        using var owner = client;
        var created = await CreateAsync(owner);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var hub = host.Hub(path, created.GetProperty("token").GetString()!))
        {
            hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
            try
            {
                await hub.StartAsync();
                await closed.Task.WaitAsync(Patience);
            }
            catch (Exception error) when (error is not TimeoutException)
            {
                // Refusal during negotiation/handshake or a server abort after connect are both valid.
            }
            Assert.NotEqual(HubConnectionState.Connected, hub.State);
        }
        await using var interactive = host.Hub(path, loginToken);
        await interactive.StartAsync();
        Assert.Equal(HubConnectionState.Connected, interactive.State);
    }

    [Fact]
    public async Task Management_audit_records_ids_without_names_prefixes_hashes_or_secrets()
    {
        var (client, _) = await host.NewUserAsync();
        using var owner = client;
        var name = "private-pipeline-" + Guid.NewGuid().ToString("n");
        var created = await CreateAsync(owner, name);
        var id = created.GetProperty("id").GetString()!;
        var token = created.GetProperty("token").GetString()!;
        var prefix = created.GetProperty("prefix").GetString()!;
        var hash = (await StoredAsync(id)).TokenHash;
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/auth/tokens/{id}")).StatusCode);

        var audit = await owner.GetFromJsonAsync<JsonElement>("/audit?limit=100");
        foreach (var action in new[] { "api-token.create", "api-token.revoke" })
        {
            var entry = Assert.Single(audit.EnumerateArray(), e => e.GetProperty("action").GetString() == action);
            Assert.Contains(id, entry.GetRawText());
        }
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        var userId = (await StoredAsync(id)).UserId;
        var persisted = JsonSerializer.Serialize(await db.AuditEvents.AsNoTracking().Where(e => e.UserId == userId).ToListAsync());
        foreach (var secret in new[] { name, prefix, token, hash })
        {
            Assert.DoesNotContain(secret, audit.GetRawText());
            Assert.DoesNotContain(secret, persisted);
        }
    }

    [Fact]
    public async Task An_existing_database_gains_the_token_table_without_losing_users_or_login_sessions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<CerberusDbContext>().UseSqlite(connection).Options;
        await using var db = new CerberusDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var user = new User { Email = "retained@example.test", PasswordHash = "retained-password-hash" };
        db.Users.Add(user);
        db.AuthSessions.Add(new AuthSession { UserId = user.Id, Token = "retained-login", ExpiresAt = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();
        // Only this private, in-memory database is changed to the pre-token schema.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE \"ApiTokens\"");
        db.ChangeTracker.Clear();

        SchemaUpgrader.EnsureUpToDate(db);
        SchemaUpgrader.EnsureUpToDate(db); // A second start must remain harmless.

        var retained = Assert.Single(await db.Users.AsNoTracking().ToListAsync());
        Assert.Equal(user.Id, retained.Id);
        Assert.Equal("retained-password-hash", retained.PasswordHash);
        Assert.Equal(user.Id, await new AuthService(db).ValidateSessionTokenAsync("retained-login"));
        var service = new ApiTokenService(db);
        var created = await service.CreateAsync(user.Id, "First pipeline after upgrade");
        Assert.Equal(user.Id, await service.ValidateAsync(created.Token));
        Assert.Equal(created.Info.Id, Assert.Single(await db.ApiTokens.AsNoTracking().ToListAsync()).Id);
    }

    [Fact]
    public async Task Authentication_can_resolve_a_hash_before_sign_in_and_expiry_is_exclusive()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var owner = new DataOwner();
        var options = new DbContextOptionsBuilder<CerberusDbContext>().UseSqlite(connection).Options;
        await using var db = new CerberusDbContext(options, owner);
        await db.Database.EnsureCreatedAsync();
        var user = new User { Email = "clock@example.test", PasswordHash = "unused" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var clock = new TokenClock(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var service = new ApiTokenService(db, clock);
        var created = await service.CreateAsync(user.Id, "Expires tomorrow", 1);
        db.ChangeTracker.Clear();
        owner.RestrictToNoOne();
        Assert.Empty(await db.ApiTokens.ToListAsync());
        Assert.Empty(await service.ListAsync(user.Id));
        Assert.False(await service.RevokeAsync(user.Id, created.Info.Id));
        Assert.Equal(user.Id, await service.ValidateAsync(created.Token));
        Assert.False(db.ChangeTracker.HasChanges());

        clock.Now = new DateTimeOffset(created.Info.ExpiresAt).AddTicks(-1);
        Assert.Equal(user.Id, await service.ValidateAsync(created.Token));
        clock.Now = new DateTimeOffset(created.Info.ExpiresAt);
        Assert.Null(await service.ValidateAsync(created.Token));
        clock.Now = clock.Now.AddTicks(1);
        Assert.Null(await service.ValidateAsync(created.Token));
    }

    private sealed class TokenClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
