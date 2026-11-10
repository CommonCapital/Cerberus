using Cerberus.Api;
using Cerberus.Api.Auth;
using Cerberus.Api.Hubs;
using Cerberus.Api.Settings;
using Cerberus.Compliance;
using Cerberus.Core.Models;
using Cerberus.Data;
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
app.MapHub<ChatHub>("/hub/chat");

var port = Environment.GetEnvironmentVariable("PORT") ?? "3000";
app.Run($"http://localhost:{port}");

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
