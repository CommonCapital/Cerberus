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

// Modes of this binary other than serving (see ServiceCommands). `health` is what a container's healthcheck runs.
if (args is ["health", ..])
{
    using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    var url = args.ElementAtOrDefault(1) ?? $"http://localhost:{Environment.GetEnvironmentVariable("PORT") ?? "3000"}";
    return await Cerberus.Api.Health.ServiceCommands.HealthAsync(url, Console.Out, probe, CancellationToken.None);
}

// A setting that is present but cannot be understood stops the service: it is not replaced by a default.
var configErrors = Cerberus.Api.Security.ServiceConfig.Validate(Environment.GetEnvironmentVariable);
if (configErrors.Count > 0)
{
    Console.Error.WriteLine("Cerberus cannot start: the configuration is not valid.");
    foreach (var error in configErrors) Console.Error.WriteLine("  - " + error);
    return 2;
}

var builder = WebApplication.CreateBuilder(args);
// Nothing in a response needs to say which server software this is.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

// --- The service's own protection: request size, request rate, browser origins (§22 item 8) ---
Cerberus.Api.Security.ServiceHardening.AddServiceHardening(
    builder.Services, Cerberus.Api.Security.ServiceLimits.FromEnvironment(Environment.GetEnvironmentVariable));

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
Cerberus.Api.Health.HealthEndpoints.AddReadiness(builder.Services, keyRingPath);

// --- Model providers (user-supplied keys / local Ollama) -------------------
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<IModelProvider, AnthropicProvider>();
builder.Services.AddSingleton<IModelProvider, OpenAiProvider>();
builder.Services.AddSingleton<IModelProvider, DeepSeekProvider>();
builder.Services.AddSingleton<IModelProvider, OllamaProvider>();
builder.Services.AddSingleton<IModelProvider, MockProvider>();
// A passing provider failure (overloaded, rate-limited, network) is retried with backoff before it ends a reply.
builder.Services.AddSingleton(RetryPolicy.Standard);
builder.Services.AddSingleton<IModelProviderResolver, ModelProviderResolver>();
builder.Services.AddSingleton<ILocalModelCatalog, OllamaCatalog>();

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
// A conversation over the queue: a turn is accepted, queued under its user's id, answered by a worker and delivered over SignalR.
builder.Services.AddScoped<Cerberus.Api.Chat.ChatService>();
builder.Services.AddSingleton(new Cerberus.Api.Chat.ChatTurnOptions(
    Concurrency: Math.Clamp(EnvInt("CERBERUS_CHAT_CONCURRENCY", 4), 1, 64),
    PerUser: Math.Clamp(EnvInt("CERBERUS_CHAT_PER_USER", 2), 1, 16),
    Timeout: TimeSpan.FromSeconds(EnvDouble("CERBERUS_CHAT_TURN_TIMEOUT_SECONDS", 900))));
builder.Services.AddSingleton<Cerberus.Api.Chat.TurnRegistry>();
builder.Services.AddSingleton<Cerberus.Api.Chat.ChatWorkerHealth>();
builder.Services.AddHostedService<Cerberus.Api.Chat.ChatTurnWorker>();
builder.Services.AddSingleton<Cerberus.Api.Chat.ChatCompactor>();
builder.Services.AddSingleton<Cerberus.Api.Chat.SessionTitler>();
builder.Services.AddSingleton(new Cerberus.Api.Chat.ChatLimits
{
    ContextTokens = int.TryParse(Environment.GetEnvironmentVariable("CERBERUS_CONTEXT_TOKENS"), out var contextTokens) && contextTokens >= 1000 ? contextTokens : null,
});

// --- Authorization & safety spine: append-only, hash-chained audit log ------
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton(new RunPolicy(
    RequireEngagement: Environment.GetEnvironmentVariable("CERBERUS_REQUIRE_ENGAGEMENT") == "true"));
// Proof-of-control probe for host/domain targets (DNS TXT lookup, well-known file fetch).
builder.Services.AddSingleton(new OwnershipProbeOptions(
    AllowLocalAddresses: Environment.GetEnvironmentVariable("CERBERUS_ALLOW_LOCAL_TARGETS") == "true"));
builder.Services.AddSingleton<Cerberus.Authorization.IOwnershipProbe, NetworkOwnershipProbe>();

// --- Job pipeline (in-process bus locally; RabbitMQ in production) ----------
// With a broker configured, work travels over RabbitMQ; without one, inside this process.
var rabbit = new RabbitMqOptions(Environment.GetEnvironmentVariable("CERBERUS_RABBITMQ_URL"));
builder.Services.AddSingleton(rabbit);
if (rabbit.Enabled) builder.Services.AddSingleton<IMessageBus, RabbitMqBus>();
else builder.Services.AddSingleton<IMessageBus, InProcMessageBus>();
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
// The team: a lead agent that dispatches specialists, several at a time with an API model.
builder.Services.AddSingleton(new Cerberus.Api.Agents.TeamOptions(
    Parallelism: Math.Clamp(EnvInt("CERBERUS_AGENT_PARALLELISM", 2), 1, Cerberus.Api.Agents.TeamOptions.MaxParallelism)));
builder.Services.AddSingleton<Cerberus.Api.Agents.ISpecialistRunner, Cerberus.Api.Agents.ScopedSpecialistRunner>();
builder.Services.AddScoped<Cerberus.Api.Agents.LeadAgentRunner>();

// --- Semantic code search: a local embedding model, vectors in the local database ---
builder.Services.AddSingleton(new Cerberus.Api.Search.EmbeddingOptions(
    Model: Environment.GetEnvironmentVariable("CERBERUS_EMBEDDING_MODEL") ?? "nomic-embed-text",
    BaseUrl: Environment.GetEnvironmentVariable("CERBERUS_EMBEDDING_URL") ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434/v1",
    AllowRemote: Environment.GetEnvironmentVariable("CERBERUS_EMBEDDING_ALLOW_REMOTE") == "true"));
builder.Services.AddSingleton<Cerberus.Api.Search.IEmbedder, Cerberus.Api.Search.OllamaEmbedder>();
builder.Services.AddSingleton<Cerberus.Api.Search.CodeSearch>();
builder.Services.AddScoped<Cerberus.Api.Search.CodeIndexer>();
// --- Internet access for agents: off unless switched on; search only through a self-hosted SearXNG ---
builder.Services.AddSingleton(new Cerberus.Api.Web.WebOptions(
    Enabled: Environment.GetEnvironmentVariable("CERBERUS_WEB") == "on",
    SearchUrl: Environment.GetEnvironmentVariable("CERBERUS_SEARXNG_URL"),
    BlockedTerms: (Environment.GetEnvironmentVariable("CERBERUS_EGRESS_BLOCKED_TERMS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
builder.Services.AddSingleton<Cerberus.Api.Web.IWebFetcher, Cerberus.Api.Web.SafeFetcher>();
builder.Services.AddSingleton<Cerberus.Api.Web.IWebSearch, Cerberus.Api.Web.SearxngSearch>();
// A browser for the researcher: its own switch on top of web access, an empty profile per page, one way out.
builder.Services.AddSingleton(new Cerberus.Api.Web.BrowserOptions(
    Enabled: Environment.GetEnvironmentVariable("CERBERUS_BROWSER") == "on",
    Path: Environment.GetEnvironmentVariable("CERBERUS_BROWSER_PATH"),
    NoSandbox: Environment.GetEnvironmentVariable("CERBERUS_BROWSER_NO_SANDBOX") == "true"));
builder.Services.AddSingleton<Cerberus.Api.Web.IAgentBrowser, Cerberus.Api.Web.ChromeBrowser>();
// MCP servers: named in a file by the administrator; their allowed tools go to the researcher, nowhere else.
builder.Services.AddSingleton(Cerberus.Api.Web.McpOptions.Load(
    Environment.GetEnvironmentVariable("CERBERUS_MCP_CONFIG"), Environment.GetEnvironmentVariable));
builder.Services.AddSingleton<Cerberus.Api.Web.IMcpServers, Cerberus.Api.Web.McpServers>();
builder.Services.AddSingleton<Cerberus.Api.Health.IReadinessCheck, Cerberus.Api.Health.McpCheck>();
builder.Services.AddSingleton<Cerberus.Api.Web.ResearchTools>();
builder.Services.AddSingleton<RunStream>();
builder.Services.AddSingleton<JobEvents>();
builder.Services.AddSingleton<OperatorDesk>();
builder.Services.AddScoped<JobControl>();
builder.Services.AddHostedService<JobWorker>();
// Recurring runs: due schedules are submitted through the same path as a request from the operator.
builder.Services.AddSingleton<ScheduleRunner>();
builder.Services.AddHostedService<SchedulerService>();

// Serialize enums (e.g. Severity) as strings in JSON responses.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// `web-check <url>`: fetch one page as a researcher would, to see that web access works and what it refuses.
if (args is ["web-check", ..])
{
    return await Cerberus.Api.Health.ServiceCommands.WebCheckAsync(
        app.Services.GetRequiredService<Cerberus.Api.Web.IWebFetcher>(), app.Services.GetRequiredService<Cerberus.Api.Web.WebOptions>(),
        args.ElementAtOrDefault(1), Console.Out, CancellationToken.None);
}

// `browser-check <url> [screens] [file.png]`: open one page in the agents' browser, scroll, and optionally save the screenshot.
if (args is ["browser-check", ..])
{
    return await Cerberus.Api.Health.ServiceCommands.BrowserCheckAsync(
        app.Services.GetRequiredService<Cerberus.Api.Web.IAgentBrowser>(), app.Services.GetRequiredService<Cerberus.Api.Web.BrowserOptions>(),
        args.ElementAtOrDefault(1), int.TryParse(args.ElementAtOrDefault(2), out var screens) ? screens : 0, args.ElementAtOrDefault(3),
        Console.Out, CancellationToken.None);
}

// `mcp-check [server [tool [json-arguments]]]`: reach the configured MCP servers, show what agents would see, optionally call one tool.
if (args is ["mcp-check", ..])
{
    return await Cerberus.Api.Health.ServiceCommands.McpCheckAsync(
        app.Services.GetRequiredService<Cerberus.Api.Web.IMcpServers>(), app.Services.GetRequiredService<Cerberus.Api.Web.WebOptions>(),
        args.ElementAtOrDefault(1), args.ElementAtOrDefault(2), args.ElementAtOrDefault(3), Console.Out, CancellationToken.None);
}

// `smoke-llm <model>`: can this model do what the agents need? Keys come from the environment; nothing is served.
if (args is ["smoke-llm", ..])
{
    return await Cerberus.Api.Health.ServiceCommands.SmokeAsync(
        app.Services.GetRequiredService<Cerberus.Api.Agents.AgentLoop>(), args.ElementAtOrDefault(1),
        new ModelCredentials(
            Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"), Environment.GetEnvironmentVariable("OPENAI_API_KEY"),
            Environment.GetEnvironmentVariable("OLLAMA_BASE_URL"), Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")),
        Console.Out, CancellationToken.None);
}

// Security headers go on every response, the error responses below included.
Cerberus.Api.Security.ServiceHardening.UseSecurityHeaders(app);

// Errors are JSON, as clients of this API expect: a request that cannot be read is a
// 400 "Invalid request body" (413 when it is too large), and anything unexpected is a 500 without internals.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
    {
        var tooLarge = ex.StatusCode == StatusCodes.Status413PayloadTooLarge;
        context.Response.StatusCode = tooLarge ? StatusCodes.Status413PayloadTooLarge : StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = tooLarge ? "Request body too large." : "Invalid request body" });
    }
    catch (Exception ex) when (ex is not OperationCanceledException && !context.Response.HasStarted)
    {
        app.Logger.LogError(ex, "Unhandled server error");
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "Internal server error" });
    }
});

// Browser origins, the request-size cap and the rate limits apply before any endpoint runs.
Cerberus.Api.Security.ServiceHardening.UseServiceHardening(app);

// Create the schema on first run, and additively upgrade an existing local database.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
    SchemaUpgrader.EnsureUpToDate(db);
}

app.MapCerberusEndpoints();
app.MapEngagementEndpoints();
app.MapJobEndpoints();
Cerberus.Api.Chat.ChatTurnEndpoints.MapChatTurnEndpoints(app);
app.MapScheduleEndpoints();
app.MapModelEndpoints();
Cerberus.Api.Web.ArtifactEndpoints.MapArtifactEndpoints(app);
Cerberus.Api.Web.McpEndpoints.MapMcpEndpoints(app);
app.MapReviewEndpoints();
Cerberus.Api.Reports.SarifEndpoints.MapSarifEndpoints(app);
Cerberus.Api.Agents.AgentRunEndpoints.MapAgentRunEndpoints(app);
app.MapApprovalEndpoints();
app.MapQuestionEndpoints();
app.MapSkillEndpoints();
app.MapPermissionEndpoints();
app.MapMemoryEndpoints();
Cerberus.Api.Search.CodeSearchEndpoints.MapCodeSearchEndpoints(app);
app.MapFindingDecisionEndpoints();
app.MapHub<ChatHub>("/hub/chat");
app.MapHub<JobsHub>("/hub/jobs");

var port = Environment.GetEnvironmentVariable("PORT") ?? "3000";
// Listens on this machine only by default. A container sets CERBERUS_BIND=0.0.0.0
// so the port can be published — and publishes it to localhost only (docker-compose.yml).
var bind = Environment.GetEnvironmentVariable("CERBERUS_BIND") ?? "localhost";
app.Run($"http://{bind}:{port}");
return 0;

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
