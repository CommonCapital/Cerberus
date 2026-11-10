using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cerberus.Compliance;

/// <summary>A control-catalog framework loaded from JSON (frameworks are data, not code — see PLAN.md §10).</summary>
public sealed record Catalog(
    string Id,
    string Name,
    string Version,
    string Description,
    [property: JsonPropertyName("frameworkAlignment")] IReadOnlyList<string> FrameworkAlignment,
    [property: JsonPropertyName("responseScale")] IReadOnlyList<string> ResponseScale,
    IReadOnlyList<CatalogSection> Sections)
{
    public IEnumerable<Control> Controls => Sections.SelectMany(s => s.Controls);

    public int ControlCount => Sections.Sum(s => s.Controls.Count);
}

public sealed record CatalogSection(
    string? Id,
    string Name,
    IReadOnlyList<Control> Controls);

public sealed record Control(
    string Id,
    string Title,
    string Requirement,
    [property: JsonPropertyName("evidenceHints")] IReadOnlyList<string>? EvidenceHints,
    [property: JsonPropertyName("autoEvaluable")] bool AutoEvaluable,
    /// <summary>Phase-1 scanner finding categories whose presence signals non-compliance. Null/empty = no automated signal.</summary>
    [property: JsonPropertyName("scannerCategories")] IReadOnlyList<string>? ScannerCategories = null);

/// <summary>A lightweight summary of a catalog for list endpoints.</summary>
public sealed record CatalogSummary(
    string Id,
    string Name,
    string Version,
    IReadOnlyList<string> FrameworkAlignment,
    int SectionCount,
    int ControlCount,
    int AutoEvaluableCount,
    /// <summary>Controls the current scanner can actually judge (have a scanner mapping).</summary>
    int ScannerMappedCount);

/// <summary>
/// Loads built-in control catalogs from the <c>catalogs/</c> directory (shipped as data).
/// Catalogs are parsed once and cached; the loader is registered as a singleton.
/// </summary>
public sealed class CatalogLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _catalogRoot;
    private readonly Dictionary<string, Catalog> _catalogs;

    public CatalogLoader(CatalogOptions options)
    {
        _catalogRoot = Path.GetFullPath(options.CatalogRoot);
        _catalogs = LoadAll(_catalogRoot);
    }

    public IReadOnlyCollection<Catalog> All => _catalogs.Values;

    public Catalog? Get(string id) => _catalogs.GetValueOrDefault(id);

    public static CatalogSummary Summarize(Catalog c) => new(
        c.Id, c.Name, c.Version, c.FrameworkAlignment,
        c.Sections.Count, c.ControlCount,
        c.Controls.Count(x => x.AutoEvaluable),
        c.Controls.Count(x => x.ScannerCategories is { Count: > 0 }));

    private static Dictionary<string, Catalog> LoadAll(string root)
    {
        var result = new Dictionary<string, Catalog>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return result;

        foreach (var file in Directory.GetFiles(root, "*.json"))
        {
            Catalog? catalog;
            try
            {
                catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(file), JsonOptions);
            }
            catch (JsonException)
            {
                continue; // skip non-catalog / malformed JSON
            }
            if (catalog is { Id.Length: > 0 } && catalog.Sections is { Count: > 0 })
            {
                result[catalog.Id] = catalog;
            }
        }
        return result;
    }
}

public sealed record CatalogOptions(string CatalogRoot);
