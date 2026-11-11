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
    ?? ResolveCatalogRoot(builder.Environment.ContentRootPath);
builder.Services.AddSingleton(new CatalogOptions(catalogRoot));
builder.Services.AddSingleton<CatalogLoader>();
builder.Services.AddSingleton<ComplianceEngine>();

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
    MaxQueuedPerUser: 50));
builder.Services.AddScoped<ScanRunner>();
builder.Services.AddScoped<AssessmentRunner>();
builder.Services.AddSingleton<Cerberus.Triage.FindingTriager>();
builder.Services.AddScoped<TriageRunner>();
builder.Services.AddSingleton<RunStream>();
builder.Services.AddSingleton<JobEvents>();
builder.Services.AddScoped<JobControl>();
builder.Services.AddHostedService<JobWorker>();

// Serialize enums (e.g. Severity) as strings in JSON responses.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Create the schema on first run, and additively upgrade an existing local database.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
    SchemaUpgrader.EnsureUpToDate(db);
}

app.MapCerberusEndpoints();
app.MapEngagementEndpoints();
app.MapJobEndpoints();
app.MapApprovalEndpoints();
app.MapHub<ChatHub>("/hub/chat");
app.MapHub<JobsHub>("/hub/jobs");

var port = Environment.GetEnvironmentVariable("PORT") ?? "3000";
app.Run($"http://localhost:{port}");

static int EnvInt(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;

static double EnvDouble(string name, double fallback) =>
    double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

// Walk up from the app's content root to find the repo's catalogs/ directory.
static string ResolveCatalogRoot(string contentRoot)
{
    var dir = new DirectoryInfo(contentRoot);
    while (dir is not null)
    {
        var candidate = Path.Combine(dir.FullName, "catalogs");
        if (Directory.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    // Fall back to a catalogs/ next to the content root (created on demand / empty).
    return Path.Combine(contentRoot, "catalogs");
}

// Exposed so the integration tests can host the API in-process.
public partial class Program;
