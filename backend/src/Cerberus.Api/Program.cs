using Cerberus.Api;
using Cerberus.Api.Auth;
using Cerberus.Api.Hubs;
using Cerberus.Core.Models;
using Cerberus.Data;
using Cerberus.Models;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Database: local SQLite (no hosted Postgres) ---------------------------
var dbPath = Environment.GetEnvironmentVariable("CERBERUS_DB")
    ?? Path.Combine(builder.Environment.ContentRootPath, "cerberus.db");
builder.Services.AddDbContext<CerberusDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// --- Auth ------------------------------------------------------------------
builder.Services.AddScoped<AuthService>();

// --- Model providers (user-supplied keys / local Ollama) -------------------
builder.Services.AddSingleton<HttpClient>();
builder.Services.AddSingleton<IModelProvider, AnthropicProvider>();
builder.Services.AddSingleton<IModelProvider, OpenAiProvider>();
builder.Services.AddSingleton<IModelProvider, OllamaProvider>();
builder.Services.AddSingleton<IModelProviderResolver, ModelProviderResolver>();

builder.Services.AddSignalR();

var app = builder.Build();

// Create the database/schema on first run (no migrations needed for local dev).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CerberusDbContext>();
    db.Database.EnsureCreated();
}

app.MapCerberusEndpoints();
app.MapHub<ChatHub>("/hub/chat");

var port = Environment.GetEnvironmentVariable("PORT") ?? "3000";
app.Run($"http://localhost:{port}");
