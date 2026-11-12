using System.Text.Json;
using Cerberus.Api.Auth;
using Cerberus.Api.Settings;
using Cerberus.Core.Models;

namespace Cerberus.Api;

/// <summary>The models installed on the local model server.</summary>
public interface ILocalModelCatalog
{
    /// <summary>The installed models' names, or null when the server does not answer.</summary>
    Task<IReadOnlyList<string>?> InstalledAsync(CancellationToken ct);
}

/// <summary>
/// Asks Ollama which models are pulled (its OpenAI-compatible <c>/v1/models</c>). Nothing is loaded
/// or run by asking. Embedding models are left out: they cannot hold a conversation.
/// </summary>
public sealed class OllamaCatalog(HttpClient http) : ILocalModelCatalog
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyList<string>?> InstalledAsync(CancellationToken ct)
    {
        var baseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? "http://localhost:11434/v1";
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/models", limit.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(limit.Token));
            if (!body.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
            return data.EnumerateArray()
                .Select(m => m.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(name => !string.IsNullOrWhiteSpace(name) && !name!.Contains("embed", StringComparison.OrdinalIgnoreCase))
                .Select(name => name!)
                .Order(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }
}

/// <summary>
/// What the operator can choose from (PLAN.md §6): every provider, whether it can be used right now
/// (a key is there; the local server answers), the models offered for it, and how to name any other
/// model of that provider. The list is an offer, not a limit — see <see cref="ModelRegistry"/>.
/// </summary>
public static class ModelEndpoints
{
    public static void MapModelEndpoints(this WebApplication app)
    {
        app.MapGet("/models", List).AddEndpointFilter<RequireAuthFilter>();
    }

    private static readonly (ModelProvider Provider, string Id, string Name, string EnvKey)[] Api =
    [
        (ModelProvider.Anthropic, "anthropic", "Claude (Anthropic API)", "ANTHROPIC_API_KEY"),
        (ModelProvider.OpenAI, "openai", "GPT (OpenAI API)", "OPENAI_API_KEY"),
        (ModelProvider.DeepSeek, "deepseek", "DeepSeek (DeepSeek API)", "DEEPSEEK_API_KEY"),
    ];

    /// <summary>The id to use for an installed local model: the listed id where there is one, else <c>ollama:name</c>.</summary>
    public static string LocalId(string installed)
    {
        var bare = installed.EndsWith(":latest", StringComparison.Ordinal) ? installed[..^":latest".Length] : installed;
        return ModelRegistry.Models.Any(m => m.Provider == ModelProvider.Ollama && m.Id == bare) ? bare : $"ollama:{installed}";
    }

    private static async Task<IResult> List(HttpContext http, ApiKeyService keys, ILocalModelCatalog local, IHostEnvironment environment, CancellationToken ct)
    {
        var userId = (string)http.Items[RequireAuthFilter.UserIdItem]!;
        var stored = await keys.ListProvidersAsync(userId, ct);
        var installed = await local.InstalledAsync(ct);

        var providers = new List<object>();
        foreach (var (provider, id, name, envKey) in Api)
        {
            var own = stored.Contains(id);
            var shared = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(envKey));
            providers.Add(new
            {
                id, name, kind = "api",
                // Usable when the operator stored a key, or the server was started with one.
                ready = own || shared,
                keySource = own ? "yours" : shared ? "server" : null,
                models = ModelRegistry.Models.Where(m => m.Provider == provider).Select(m => m.Id),
                anyModel = $"{id}:<model>",
            });
        }

        var listedLocal = ModelRegistry.Models.Where(m => m.Provider == ModelProvider.Ollama).Select(m => m.Id).ToList();
        providers.Add(new
        {
            id = "ollama", name = "Ollama (local models)", kind = "local",
            ready = installed is { Count: > 0 },
            // Whether the local server answered at all; with it down, the listed names are only suggestions.
            reachable = installed is not null,
            models = installed is null ? listedLocal : installed.Select(LocalId).Distinct().ToList(),
            anyModel = "ollama:<model>",
        });

        if (environment.IsDevelopment())
        {
            providers.Add(new { id = "dev", name = "Built-in test model (no model behind it)", kind = "local", ready = true, models = new[] { "echo" } });
        }

        return Results.Json(new { @default = ModelRegistry.DefaultModelId, providers });
    }
}
