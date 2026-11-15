using Cerberus.Api.Agents;
using Cerberus.Core.Models;
using Cerberus.Triage;

namespace Cerberus.Api.Controls;

/// <summary>
/// The kill switch for kinds of tools: an installation can switch whole groups of capability off,
/// for every user and every agent, whatever a request or a model asks for (CERBERUS_DISABLED_TOOLS). A group
/// that is off is not offered at all — the model never learns it exists — and where a feature depends on it
/// (research on the internet, outside servers) the feature is off too. An unknown name stops the service at
/// start-up: a typing mistake must not leave on what someone meant to switch off.
/// </summary>
public sealed class DisabledTools
{
    public const string Variable = "CERBERUS_DISABLED_TOOLS";

    /// <summary>Every group that can be switched off, and what it covers — the operator reads this in the error.</summary>
    public static readonly IReadOnlyDictionary<string, string> Groups = new Dictionary<string, string>
    {
        ["web"] = "fetching and searching the public internet (the researcher's webFetch and webSearch)",
        ["browser"] = "the researcher's browser",
        ["mcp"] = "outside tool servers (MCP)",
        ["shell"] = "the chat agent's shell commands",
        ["file-edits"] = "the chat agent's writing and editing of files",
        ["helpers"] = "the chat agent's helpers (task)",
        ["notes"] = "agents' notes for later runs (remember)",
        ["skills"] = "the skills library (searchSkills, loadSkill)",
        ["questions"] = "agents' questions to the operator (askOperator)",
    };

    public static DisabledTools None { get; } = new([]);

    private readonly HashSet<string> _off;

    private DisabledTools(IEnumerable<string> off) => _off = new HashSet<string>(off, StringComparer.Ordinal);

    /// <summary>The groups that are off, in a fixed order.</summary>
    public IReadOnlyList<string> Off => Groups.Keys.Where(_off.Contains).ToList();

    public bool IsOff(string group) =>
        Groups.ContainsKey(group) ? _off.Contains(group) : throw new ArgumentException($"There is no tool group \"{group}\".", nameof(group));

    private static IEnumerable<string> Names(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => n.ToLowerInvariant());

    /// <summary>What is wrong with the setting, or null. Called at start-up, before anything else (see ServiceConfig).</summary>
    public static string? Problem(string? value)
    {
        var unknown = Names(value).Where(n => !Groups.ContainsKey(n)).Distinct().ToList();
        return unknown.Count == 0
            ? null
            : $"{Variable} names {(unknown.Count == 1 ? "a tool group" : "tool groups")} that do{(unknown.Count == 1 ? "es" : "")} not exist: " +
              $"{string.Join(", ", unknown)}. The groups are: {string.Join(", ", Groups.Keys)}.";
    }

    /// <summary>Reads the setting. Only call it once <see cref="Problem"/> has passed.</summary>
    public static DisabledTools Parse(string? value) =>
        Problem(value) is { } problem ? throw new InvalidOperationException(problem) : new DisabledTools(Names(value));

    /// <summary>Of what an agent's description offers, what remains with these groups off.</summary>
    public AgentOffers Allow(AgentOffers offers)
    {
        if (_off.Contains("notes")) offers &= ~AgentOffers.Notes;
        if (_off.Contains("skills")) offers &= ~AgentOffers.Skills;
        if (_off.Contains("questions")) offers &= ~AgentOffers.Questions;
        return offers;
    }

    /// <summary>Of the chat agent's tools, those that remain with these groups off.</summary>
    public IReadOnlyList<ToolDefinition> Allow(IReadOnlyList<ToolDefinition> tools) => tools.Where(t => GroupOf(t.Name) is not { } group || !_off.Contains(group)).ToList();

    private static string? GroupOf(string tool) => tool switch
    {
        "bash" => "shell",
        "writeFile" or "editFile" => "file-edits",
        Chat.ChatTools.Task => "helpers",
        Chat.ChatTools.SearchSkills or Chat.ChatTools.LoadSkill => "skills",
        _ => null,
    };
}

/// <summary>Limits on every agent run that the installation sets.</summary>
/// <param name="TimeLimit">
/// How long one agent run may take before it is stopped and asked for its summary (CERBERUS_AGENT_TIME_LIMIT_SECONDS).
/// Null: no limit of its own — the job's time limit still applies, but that one ends the run without a summary.
/// </param>
public sealed record AgentLimits(TimeSpan? TimeLimit = null);

/// <summary>
/// Which model does the small jobs around a conversation — its title, the summary of its older part — when the
/// installation names a cheaper one (CERBERUS_FAST_MODEL). The conversation's
/// own model is used instead whenever the fast one would change where the conversation's text goes:
/// <list type="bullet">
/// <item>a fast model on this machine (Ollama) may always do it;</item>
/// <item>a fast model of an API provider only when the conversation already goes to that same provider, and a
/// key for it is at hand — a conversation with a local model never leaves this machine for a title;</item>
/// <item>for a summary, only a fast model that can read at least as much as the conversation's own — otherwise
/// what needs summarising might not fit.</item>
/// </list>
/// </summary>
public sealed class ModelRoles(string? fastModel)
{
    public const string Variable = "CERBERUS_FAST_MODEL";

    public string? FastModel { get; } = string.IsNullOrWhiteSpace(fastModel) ? null : fastModel.Trim();

    public static string? Problem(string? value) =>
        string.IsNullOrWhiteSpace(value) || ModelRegistry.IsSupported(value.Trim())
            ? null
            : $"{Variable} must be a model this server knows: {ModelRegistry.Accepted}.";

    /// <summary>The model to write a conversation's title with.</summary>
    public string ForTitle(string conversationModel, ModelCredentials credentials) => Decide(conversationModel, credentials, summary: false).Model;

    /// <summary>The model to summarise a conversation's older part with.</summary>
    public string ForSummary(string conversationModel, ModelCredentials credentials) => Decide(conversationModel, credentials, summary: true).Model;

    /// <summary>The choice, and why — for tests and for the operator.</summary>
    public (string Model, string Why) Decide(string conversationModel, ModelCredentials credentials, bool summary)
    {
        if (FastModel is null) return (conversationModel, "no fast model is configured");
        if (string.Equals(FastModel, conversationModel, StringComparison.OrdinalIgnoreCase)) return (conversationModel, "it is the conversation's own model");
        var fast = ModelRegistry.Find(FastModel)?.Provider;
        var own = ModelRegistry.Find(conversationModel)?.Provider;
        if (fast is null || own is null) return (conversationModel, "one of the two models is not known");

        if (!FindingTriager.IsLocal(fast.Value))
        {
            if (fast != own) return (conversationModel, $"the fast model would send the conversation to {fast}, where it does not go now");
            if (!HasKey(fast.Value, credentials)) return (conversationModel, $"there is no key for {fast}");
        }
        if (summary && ModelRegistry.ContextWindowOf(FastModel) < ModelRegistry.ContextWindowOf(conversationModel))
        {
            return (conversationModel, "the fast model reads less than the conversation's own, and the summary might not fit");
        }
        return (FastModel, "the fast model");
    }

    private static bool HasKey(ModelProvider provider, ModelCredentials credentials) => provider switch
    {
        ModelProvider.Anthropic => !string.IsNullOrWhiteSpace(credentials.AnthropicApiKey),
        ModelProvider.OpenAI => !string.IsNullOrWhiteSpace(credentials.OpenAiApiKey),
        ModelProvider.DeepSeek => !string.IsNullOrWhiteSpace(credentials.DeepSeekApiKey),
        _ => true,
    };
}

/// <summary>Says which groups of tools are switched off. Always healthy: switching something off is a decision, not a fault.</summary>
public sealed class DisabledToolsCheck(DisabledTools disabled) : Health.IReadinessCheck
{
    public string Name => "tools";
    public bool Required => false;

    public Task<(bool, string)> CheckAsync(CancellationToken ct) => Task.FromResult((true,
        disabled.Off.Count == 0 ? "all tool groups are on" : $"switched off by {DisabledTools.Variable}: {string.Join(", ", disabled.Off)}"));
}
