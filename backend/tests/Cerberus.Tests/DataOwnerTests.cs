using System.Net.Http.Json;
using System.Text.Json;
using Cerberus.Api.Auth;
using Cerberus.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cerberus.Tests;

/// <summary>
/// The second lock on per-user data: a database context restricted to one
/// user returns that user's rows whatever the query says. Every query below
/// deliberately has no condition on the user — that is the mistake the lock
/// is there for.
/// </summary>
public sealed class DataOwnerTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "cerberus-owner-" + Guid.NewGuid().ToString("n") + ".db");
    private readonly DbContextOptions<CerberusDbContext> _options;

    private const string Alice = "user-alice", Bob = "user-bob";

    public DataOwnerTests()
    {
        _options = new DbContextOptionsBuilder<CerberusDbContext>().UseSqlite($"Data Source={_file};Pooling=False").Options;
        using var db = new CerberusDbContext(_options);   // no owner: unrestricted
        db.Database.EnsureCreated();

        foreach (var user in new[] { Alice, Bob })
        {
            db.Users.Add(new User { Id = user, Email = user + "@test.dev", PasswordHash = "x" });
            db.AuthSessions.Add(new AuthSession { UserId = user, Token = "token-" + user, ExpiresAt = DateTime.UtcNow.AddDays(1) });
            db.Scans.Add(new Scan { Id = "scan-" + user, UserId = user, TargetPath = "app" });
            db.Scans.Add(new Scan { Id = "scan2-" + user, UserId = user, TargetPath = "svc" });
            db.Sessions.Add(new Session { Id = "session-" + user, UserId = user, Title = "chat" });
            db.Engagements.Add(new Engagement
            {
                Id = "eng-" + user, UserId = user, Name = "Engagement",
                Targets = [new EngagementTarget { Id = "target-" + user, Locator = "app" }],
            });
            db.CodeIndexes.Add(new CodeIndex { Id = "index-" + user, UserId = user, TargetPath = "app", Model = "m" });
            db.CodeChunks.Add(new CodeChunk { IndexId = "index-" + user, File = "a.py", Name = "chunk of " + user });
        }
        db.SaveChanges();
    }

    public void Dispose()
    {
        try { File.Delete(_file); } catch (IOException) { }
    }

    private CerberusDbContext Context(DataOwner owner) => new(_options, owner);

    private static DataOwner For(string? user)
    {
        var owner = new DataOwner();
        if (user is null) owner.RestrictToNoOne(); else owner.RestrictTo(user);
        return owner;
    }

    [Fact]
    public void An_unrestricted_context_sees_everyone()
    {
        using var db = Context(new DataOwner());
        Assert.Equal(4, db.Scans.Count());
        Assert.Equal(2, db.EngagementTargets.Count());
        Assert.Equal(2, db.CodeChunks.Count());
    }

    [Fact]
    public void A_query_that_forgets_the_user_returns_only_the_owners_rows()
    {
        using var db = Context(For(Alice));

        Assert.Equal(["scan-user-alice", "scan2-user-alice"], db.Scans.Select(s => s.Id).OrderBy(id => id).ToArray());
        Assert.Equal(["session-user-alice"], db.Sessions.Select(s => s.Id).ToArray());
        Assert.Equal(["eng-user-alice"], db.Engagements.Select(e => e.Id).ToArray());
        Assert.Equal(["index-user-alice"], db.CodeIndexes.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Asking_for_someone_elses_row_by_id_finds_nothing()
    {
        using var db = Context(For(Alice));

        Assert.Null(await db.Scans.FindAsync("scan-user-bob"));
        Assert.Null(await db.Scans.FirstOrDefaultAsync(s => s.Id == "scan-user-bob"));
        Assert.Empty(db.Scans.Where(s => s.UserId == Bob));
        Assert.NotNull(await db.Scans.FindAsync("scan-user-alice"));
    }

    [Fact]
    public void Rows_owned_through_a_parent_are_locked_too()
    {
        using var db = Context(For(Alice));

        // A target has no user of its own: it belongs to its engagement. A chunk belongs to its index.
        Assert.Equal(["target-user-alice"], db.EngagementTargets.Select(t => t.Id).ToArray());
        Assert.Empty(db.EngagementTargets.Where(t => t.EngagementId == "eng-user-bob"));
        Assert.Equal(["chunk of user-alice"], db.CodeChunks.Select(c => c.Name).ToArray());
        Assert.Empty(db.CodeChunks.Where(c => c.IndexId == "index-user-bob"));
        Assert.Equal(["target-user-alice"], db.Engagements.Include(e => e.Targets).SelectMany(e => e.Targets).Select(t => t.Id).ToArray());
    }

    [Fact]
    public void Before_sign_in_nothing_owned_is_visible_but_sign_in_itself_still_works()
    {
        using var db = Context(For(null));

        Assert.Empty(db.Scans);
        Assert.Empty(db.Sessions);
        Assert.Empty(db.Engagements);
        Assert.Empty(db.EngagementTargets);
        Assert.Empty(db.CodeChunks);
        // What checking a token needs is not owned data: an account by its email, a session by its secret token.
        Assert.Equal(2, db.Users.Count());
        Assert.NotNull(db.AuthSessions.FirstOrDefault(a => a.Token == "token-" + Alice));
    }

    [Fact]
    public void A_context_restricted_after_it_was_created_is_restricted_from_then_on()
    {
        // The order in a request: the context exists, then the token is checked, then the user is named.
        var owner = new DataOwner();
        using var db = Context(owner);
        Assert.Equal(4, db.Scans.Count());

        owner.RestrictToNoOne();
        Assert.Equal(0, db.Scans.Count());

        owner.RestrictTo(Bob);
        Assert.Equal(["scan-user-bob", "scan2-user-bob"], db.Scans.Select(s => s.Id).OrderBy(id => id).ToArray());
    }

    [Fact]
    public async Task Bulk_changes_touch_only_the_owners_rows()
    {
        using (var db = Context(For(Alice)))
        {
            Assert.Equal(2, await db.Scans.ExecuteDeleteAsync());
            Assert.Equal(1, await db.Sessions.ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, "renamed")));
        }

        using var all = Context(new DataOwner());
        Assert.Equal(["scan-user-bob", "scan2-user-bob"], all.Scans.Select(s => s.Id).OrderBy(id => id).ToArray());
        Assert.Equal("chat", all.Sessions.Single(s => s.UserId == Bob).Title);
        Assert.Equal("renamed", all.Sessions.Single(s => s.UserId == Alice).Title);
    }

    [Fact]
    public void Another_users_row_cannot_be_added_changed_or_removed()
    {
        using (var db = Context(For(Alice)))
        {
            db.Scans.Add(new Scan { UserId = Bob, TargetPath = "planted" });
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        }
        using (var db = Context(For(Alice)))
        {
            // The row cannot be loaded, but an id is enough to attach it.
            db.Scans.Remove(new Scan { Id = "scan-user-bob", UserId = Bob });
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        }
        using (var db = Context(For(Alice)))
        {
            db.Scans.Update(new Scan { Id = "scan-user-bob", UserId = Bob, TargetPath = "changed" });
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        }
        using (var db = Context(For(null)))
        {
            db.Scans.Add(new Scan { UserId = Alice, TargetPath = "before sign-in" });
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        }

        using var all = Context(new DataOwner());
        Assert.Equal(4, all.Scans.Count());
        Assert.Equal("app", all.Scans.Single(s => s.Id == "scan-user-bob").TargetPath);
    }

    [Fact]
    public async Task The_owner_writes_their_own_rows_as_before()
    {
        using (var db = Context(For(Alice)))
        {
            db.Scans.Add(new Scan { Id = "scan3", UserId = Alice, TargetPath = "new" });
            (await db.Scans.FindAsync("scan-user-alice"))!.TargetPath = "edited";
            db.Scans.Remove((await db.Scans.FindAsync("scan2-user-alice"))!);
            // A sign-in session has no owner filter and can be written before and after sign-in.
            db.AuthSessions.Add(new AuthSession { UserId = Alice, Token = "another", ExpiresAt = DateTime.UtcNow.AddDays(1) });
            Assert.Equal(4, await db.SaveChangesAsync());
        }

        using var all = Context(new DataOwner());
        Assert.Equal(["scan-user-alice", "scan3"], all.Scans.Where(s => s.UserId == Alice).Select(s => s.Id).OrderBy(id => id).ToArray());
        Assert.Equal("edited", all.Scans.Single(s => s.Id == "scan-user-alice").TargetPath);
    }

    [Fact]
    public void Looking_past_the_lock_has_to_be_written_out()
    {
        // There is a way around it, and it is a visible one in the code that uses it.
        using var db = Context(For(Alice));
        Assert.Equal(4, db.Scans.IgnoreQueryFilters().Count());
    }

    [Fact]
    public void An_owner_needs_a_user()
    {
        Assert.ThrowsAny<ArgumentException>(() => new DataOwner().RestrictTo(""));
    }
}

/// <summary>The lock as the running API applies it.</summary>
[Collection("api")]
public class DataOwnerApiTests(ApiHost host)
{
    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task A_scope_a_service_opens_for_itself_is_unrestricted_and_one_it_restricts_is_not()
    {
        var (alice, _) = await host.NewUserAsync();
        var (bob, _) = await host.NewUserAsync();
        var aliceScan = (await Json(await alice.PostAsJsonAsync("/scan", new { path = "app", sca = false }))).GetProperty("id").GetString()!;
        var bobScan = (await Json(await bob.PostAsJsonAsync("/scan", new { path = "app", sca = false }))).GetProperty("id").GetString()!;

        using var scope = host.Services.CreateScope();
        var owner = scope.ServiceProvider.GetRequiredService<DataOwner>();
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();

        // As the job worker or the audit log get it: both users' rows.
        Assert.False(owner.Restricted);
        Assert.Equal(2, await db.Scans.CountAsync(s => s.Id == aliceScan || s.Id == bobScan));

        // The context the container built is tied to the owner of its own scope.
        var aliceId = await db.Scans.Where(s => s.Id == aliceScan).Select(s => s.UserId).SingleAsync();
        owner.RestrictTo(aliceId);
        Assert.Equal([aliceScan], await db.Scans.Where(s => s.Id == aliceScan || s.Id == bobScan).Select(s => s.Id).ToArrayAsync());

        // Another scope is not affected by it.
        using var other = host.Services.CreateScope();
        Assert.False(other.ServiceProvider.GetRequiredService<DataOwner>().Restricted);
    }

    [Fact]
    public async Task Checking_the_token_restricts_the_request_to_its_user()
    {
        var (client, token) = await host.NewUserAsync();
        var scanId = (await Json(await client.PostAsJsonAsync("/scan", new { path = "app", sca = false }))).GetProperty("id").GetString()!;

        using var scope = host.Services.CreateScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Headers.Authorization = "Bearer " + token;
        var owner = scope.ServiceProvider.GetRequiredService<DataOwner>();
        owner.RestrictToNoOne();   // what the pipeline does when a request arrives

        string? seenUser = null;
        var result = await new RequireAuthFilter().InvokeAsync(
            new DefaultEndpointFilterInvocationContext(http),
            _ =>
            {
                seenUser = owner.Restricted ? owner.UserId : "(unrestricted)";
                return ValueTask.FromResult<object?>("ran");
            });

        Assert.Equal("ran", result);
        Assert.Equal(http.Items[RequireAuthFilter.UserIdItem], seenUser);
        var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
        Assert.Contains(scanId, await db.Scans.Select(s => s.Id).ToListAsync());
        Assert.All(await db.Scans.Select(s => s.UserId).ToListAsync(), user => Assert.Equal(seenUser, user));

        // A token that is not valid leaves the request restricted to no one, and the endpoint does not run.
        using var refusedScope = host.Services.CreateScope();
        var refused = new DefaultHttpContext { RequestServices = refusedScope.ServiceProvider };
        refused.Request.Headers.Authorization = "Bearer not-a-token";
        var refusedOwner = refusedScope.ServiceProvider.GetRequiredService<DataOwner>();
        refusedOwner.RestrictToNoOne();
        var ran = false;
        await new RequireAuthFilter().InvokeAsync(new DefaultEndpointFilterInvocationContext(refused), _ => { ran = true; return ValueTask.FromResult<object?>(null); });
        Assert.False(ran);
        Assert.True(refusedOwner.Restricted);
        Assert.Null(refusedOwner.UserId);
    }

    /// <summary>Appends a path the API does not have, answering with what a request's own context can see — with no condition on the user.</summary>
    private sealed class Probe : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Use(async (context, following) =>
            {
                if (context.Request.Path != "/__probe") { await following(context); return; }
                var owner = context.RequestServices.GetRequiredService<DataOwner>();
                var db = context.RequestServices.GetRequiredService<CerberusDbContext>();
                context.Response.StatusCode = 200;
                await context.Response.WriteAsJsonAsync(new
                {
                    restricted = owner.Restricted,
                    userId = owner.UserId,
                    scans = await db.Scans.CountAsync(),
                    sessions = await db.Sessions.CountAsync(),
                    users = await db.Users.CountAsync(),
                });
            });
        };
    }

    [Fact]
    public async Task A_request_that_has_not_signed_in_sees_nobodys_data()
    {
        using var probed = host.StartVariant("CERBERUS_WEB", "on")
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddTransient<IStartupFilter, Probe>()));
        using var user = await ApiHost.AuthenticatedClientAsync(probed);
        Assert.True((await user.PostAsJsonAsync("/scan", new { path = "app", sca = false })).IsSuccessStatusCode);
        Assert.True((await user.PostAsJsonAsync("/sessions", new { title = "mine" })).IsSuccessStatusCode);

        // The probe is reached without the sign-in check, even with a valid token on the request.
        var seen = await Json(await user.GetAsync("/__probe"));

        Assert.True(seen.GetProperty("restricted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("userId").ValueKind);
        Assert.Equal(0, seen.GetProperty("scans").GetInt32());
        Assert.Equal(0, seen.GetProperty("sessions").GetInt32());
        Assert.True(seen.GetProperty("users").GetInt32() >= 1);   // rows exist; the lock is why none were counted
    }
}
