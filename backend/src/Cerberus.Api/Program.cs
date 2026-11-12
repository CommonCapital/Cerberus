using Cerberus.Api;
using Cerberus.Api.Audit;
using Cerberus.Api.Auth;
using Cerberus.Api.Hubs;
using Cerberus.Api.Jobs;
using Cerberus.Api.Ownership;
using Cerberus.Api.Settings;
using Cerberus.Compliance;
using Cerberus.Core.Models;
using Cerberus.Data;
using Cerberus.Jobs;
using Cerberus.Models;
using Cerberus.Scanning;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// --- Database: local SQLite (no hosted Postgres) ---------------------------
var dbPath = Environment.GetEnvironmentVariable("CERBERUS_DB")
    ?? Path.Combine(builder.Environment.ContentRootPath, "cerberus.db");
builder.Services.AddDbContext<CerberusDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// --- Auth ------------------------------------------------------------------
builder.Services.AddScoped<AuthService>();
builder.Services.AddSingleton<LoginThrottle>();

// --- Per-user API keys (encrypted at rest via Data Protection) -------------
var keyRingPath = Environment.GetEnvironmentVariable("CERBERUS_KEYRING")
    ?? Path.Combine(builder.Environment.ContentRootPath, "dp-keys");
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
    .SetApplicationName("Cerberus");
builder.Services.AddScoped<ApiKeyService>();

// --- Model providers (user-supplied keys / local Ollama) -------------------
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<IModelProvider, AnthropicProvider>();
builder.Services.AddSingleton<IModelProvider, OpenAiProvider>();
builder.Services.AddSingleton<IModelProvider, OllamaProvider>();
builder.Services.AddSingleton<IModelProvider, MockProvider>();
// A passing provider failure (overloaded, rate-limited, network) is retried with backoff before it ends a reply.
builder.Services.AddSingleton(RetryPolicy.Standard);
builder.Services.AddSingleton<IModelProviderResolver, ModelProviderResolver>();

builder.Services.AddSignalR();

// --- Phase 1 scanning (passive, trusted-target MVP) ------------------------
var scanRoot = Environment.GetEnvironmentVariable("CERBERUS_SCAN_ROOT") ?? Directory.GetCurrentDirectory();
builder.Services.AddSingleton(new ScanOptions(scanRoot));
builder.Services.AddSingleton<ScanService>();
// Phase 1 SCA (dependency/CVE analysis via OSV.dev — passive T0, read-only).
builder.Services.AddSingleton<OsvClient>();
builder.Services.AddSingleton<ScaService>();

// --- Phase 2 compliance (ESAP/ISO/OWASP catalogs shipped as data) ----------
var catalogRoot = Environment.GetEnvironmentVariable("CERBERUS_CATALOG_ROOT")
    ?? ResolveUpwards(builder.Environment.ContentRootPath, "catalogs");
builder.Services.AddSingleton(new CatalogOptions(catalogRoot));
builder.Services.AddSingleton<CatalogLoader>();
builder.Services.AddSingleton<ComplianceEngine>();

// --- Skills library (vendored under security-skills/; agents search and load from it) ---
var skillsRoot = Environment.GetEnvironmentVariable("CERBERUS_SKILLS_ROOT")
    ?? Path.Combine(ResolveUpwards(builder.Environment.ContentRootPath, "security-skills"), "skills");
builder.Services.AddSingleton(new Cerberus.Skills.SkillOptions(skillsRoot));
builder.Services.AddSingleton<Cerberus.Skills.SkillLibrary>();
builder.Services.AddSingleton<SkillAdvisor>();
builder.Services.AddSingleton<Cerberus.Api.Chat.ChatRunner>();

// --- Authorization & safety spine: append-only, hash-chained audit log ------
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton(new RunPolicy(
    RequireEngagement: Environment.GetEnvironmentVariable("CERBERUS_REQUIRE_ENGAGEMENT") == "true"));
// Proof-of-control probe for host/domain targets (DNS TXT lookup, well-known file fetch).
builder.Services.AddSingleton(new OwnershipProbeOptions(
    AllowLocalAddresses: Environment.GetEnvironmentVariable("CERBERUS_ALLOW_LOCAL_TARGETS") == "true"));
builder.Services.AddSingleton<Cerberus.Authorization.IOwnershipProbe, NetworkOwnershipProbe>();

// --- Job pipeline (in-process bus locally; RabbitMQ in production) ----------
builder.Services.AddSingleton<IMessageBus, InProcMessageBus>();
builder.Services.AddSingleton<JobCancellationRegistry>();
builder.Services.AddSingleton(new JobOptions(
    Concurrency: Math.Clamp(EnvInt("CERBERUS_JOB_CONCURRENCY", 2), 1, 16),
    Timeout: TimeSpan.FromSeconds(EnvDouble("CERBERUS_JOB_TIMEOUT_SECONDS", 600)),
    Grace: TimeSpan.FromSeconds(EnvDouble("CERBERUS_JOB_GRACE_SECONDS", 10)),
    MaxQueuedPerUser: 50,
    QuestionTimeout: TimeSpan.FromSeconds(EnvDouble("CERBERUS_QUESTION_TIMEOUT_SECONDS", 3600))));
builder.Services.AddScoped<ScanRunner>();
builder.Services.AddScoped<AssessmentRunner>();
builder.Services.AddSingleton<Cerberus.Triage.FindingTriager>();
builder.Services.AddScoped<TriageRunner>();
builder.Services.AddScoped<ReviewRunner>();
builder.Services.AddSingleton<Cerberus.Api.Agents.AgentLoop>();
builder.Services.AddSingleton<Cerberus.Api.Agents.OperatorInbox>();
builder.Services.AddScoped<Cerberus.Api.Agents.SastAgentRunner>();
builder.Services.AddScoped<Cerberus.Api.Agents.EvidenceAgentRunner>();
builder.Services.AddScoped<Cerberus.Api.Agents.ThreatModelAgentRunner>();
builder.Services.AddSingleton<RunStream>();
builder.Services.AddSingleton<JobEvents>();
builder.Services.AddSingleton<OperatorDesk>();
builder.Services.AddScoped<JobControl>();
builder.Services.AddHostedService<JobWorker>();

// Serialize enums (e.g. Severity) as strings in JSON responses.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Errors are JSON, as clients of this API expect: a request that cannot be read is a
// 400 "Invalid request body", and anything unexpected is a 500 without internals.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (BadHttpRequestException) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "Invalid request body" });
    }
    catch (Exception ex) when (ex is not OperationCanceledException && !context.Response.HasStarted)
    {
        app.Logger.LogError(ex, "Unhandled server error");
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "Internal server error" });
    }
});

// Create the schema on first run, and additively upgrade an existing local database.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
    SchemaUpgrader.EnsureUpToDate(db);
}

app.MapCerberusEndpoints();
app.MapEngagementEndpoints();
app.MapJobEndpoints();
app.MapReviewEndpoints();
Cerberus.Api.Reports.SarifEndpoints.MapSarifEndpoints(app);
Cerberus.Api.Agents.AgentRunEndpoints.MapAgentRunEndpoints(app);
app.MapApprovalEndpoints();
app.MapQuestionEndpoints();
app.MapSkillEndpoints();
app.MapPermissionEndpoints();
app.MapMemoryEndpoints();
app.MapFindingDecisionEndpoints();
app.MapHub<ChatHub>("/hub/chat");
app.MapHub<JobsHub>("/hub/jobs");

var port = Environment.GetEnvironmentVariable("PORT") ?? "3000";
// Listens on this machine only by default. A container sets CERBERUS_BIND=0.0.0.0
// so the port can be published — and publishes it to localhost only (docker-compose.yml).
var bind = Environment.GetEnvironmentVariable("CERBERUS_BIND") ?? "localhost";
app.Run($"http://{bind}:{port}");

static int EnvInt(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

static double EnvDouble(string name, double fallback) =>
    double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

// Walk up from the app's content root to find a directory of the repository (catalogs/, security-skills/).
static string ResolveUpwards(string contentRoot, string name)
{
    var dir = new DirectoryInfo(contentRoot);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, name);
        if (Directory.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    // Not found: a path next to the content root, which simply yields an empty set.
    return Path.Combine(contentRoot, name);
}

// Exposed so the integration tests can host the API in-process.
public partial class Program;
