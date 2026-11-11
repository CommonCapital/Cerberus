using System.Text.Json;
using Cerberus.Authorization;
using Cerberus.Core.Models;
using Cerberus.Skills;

namespace Cerberus.Api.Chat;

/// <summary>
/// The tools the chat agent may call. Two kinds:
/// <list type="bullet">
/// <item><b>Client tools</b> — file and shell tools the CLI runs on the operator's
/// machine, in the project it was started in. The server only describes them
/// and relays the calls. PLAN mode offers the four read-only ones; BUILD adds
/// write, edit and shell. (Same contracts as the TypeScript server's.)</item>
/// <item><b>Server tools</b> — run here, as part of the same reply: searching and
/// loading the skills library.</item>
/// </list>
/// </summary>
public static class ChatTools
{
    public const string ModeBuild = "BUILD";
    public const string ModePlan = "PLAN";

    public const string SearchSkills = "searchSkills";
    public const string LoadSkill = "loadSkill";

    /// <summary>The tier the chat agent's skill access runs at: passive. It gets no offensive procedures.</summary>
    public const CapabilityTier SkillTier = CapabilityTier.T0;

    private static ToolDefinition Tool(string name, string description, string schema) =>
        new(name, description, JsonDocument.Parse(schema).RootElement.Clone());

    private static readonly ToolDefinition[] ReadOnly =
    [
        Tool("readFile", "Read a file from the current project directory.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Relative path to the file to read"}},"required":["path"],"additionalProperties":false}"""),
        Tool("listDirectory", "List entries in a directory under the current project directory.",
            """{"type":"object","properties":{"path":{"type":"string","default":".","description":"Relative directory path to list"}},"additionalProperties":false}"""),
        Tool("glob", "Find files matching a glob pattern under the current project directory.",
            """{"type":"object","properties":{"pattern":{"type":"string","description":"Glob pattern to match files"},"path":{"type":"string","default":".","description":"Directory to search from"}},"required":["pattern"],"additionalProperties":false}"""),
        Tool("grep", "Search file contents with a regular expression under the current project directory.",
            """{"type":"object","properties":{"pattern":{"type":"string","description":"Regex pattern to search for"},"path":{"type":"string","default":".","description":"Directory to search from"},"include":{"type":"string","description":"Optional glob for files to include"}},"required":["pattern"],"additionalProperties":false}"""),
    ];

    private static readonly ToolDefinition[] Write =
    [
        Tool("writeFile", "Create or overwrite a file under the current project directory.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Relative path to write"},"content":{"type":"string","description":"File contents"}},"required":["path","content"],"additionalProperties":false}"""),
        Tool("editFile", "Replace exact text in a file under the current project directory.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Relative path to edit"},"oldString":{"type":"string","description":"Exact text to replace; must be unique"},"newString":{"type":"string","description":"Replacement text"}},"required":["path","oldString","newString"],"additionalProperties":false}"""),
        Tool("bash", "Run a shell command in the current project directory.",
            """{"type":"object","properties":{"command":{"type":"string","description":"Shell command to run"},"description":{"type":"string","description":"Short description of the command"},"timeout":{"type":"number","description":"Timeout in milliseconds"}},"required":["command"],"additionalProperties":false}"""),
    ];

    private static readonly ToolDefinition[] Server =
    [
        Tool(SearchSkills,
            "Search the platform's security skills library for procedures relevant to a task. Returns skill names and summaries; load one with loadSkill before relying on it.",
            """{"type":"object","properties":{"query":{"type":"string","description":"Keywords, e.g. 'tls certificate' or 'incident triage'"},"limit":{"type":"number","description":"How many results to return (default 8, max 20)"}},"required":["query"],"additionalProperties":false}"""),
        Tool(LoadSkill,
            "Load the full text of one skill from the security skills library, by the exact name searchSkills returned.",
            """{"type":"object","properties":{"name":{"type":"string","description":"Exact skill name"}},"required":["name"],"additionalProperties":false}"""),
    ];

    public static bool IsValidMode(string? mode) => mode is ModeBuild or ModePlan;

    public static IReadOnlyList<ToolDefinition> For(string mode) =>
        mode == ModePlan ? [.. ReadOnly, .. Server] : [.. ReadOnly, .. Write, .. Server];

    public static bool IsServerTool(string name) => name is SearchSkills or LoadSkill;

    /// <summary>Runs a server tool. Returns the result as JSON text, or throws with a message for the model.</summary>
    public static string ExecuteServerTool(string name, JsonElement input, SkillLibrary skills)
    {
        switch (name)
        {
            case SearchSkills:
            {
                var query = input.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
                if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required.");
                var limit = input.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n) ? Math.Clamp(n, 1, 20) : 8;

                var matches = skills.Search(query, SkillTier, limit);
                return JsonSerializer.Serialize(new
                {
                    query,
                    results = matches.Select(s => new { name = s.Name, subdomain = s.Subdomain, description = s.Description }),
                    note = matches.Count == 0 ? "No skill matched. Try fewer or different keywords." : null,
                }, Json);
            }
            case LoadSkill:
            {
                var skillName = input.TryGetProperty("name", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (string.IsNullOrWhiteSpace(skillName)) throw new ArgumentException("name is required.");

                // Throws UnauthorizedAccessException for skills above this agent's tier.
                var document = skills.Read(skillName, SkillTier)
                    ?? throw new KeyNotFoundException($"No skill named \"{skillName}\". Use searchSkills to find the exact name.");
                return JsonSerializer.Serialize(new
                {
                    name = document.Summary.Name,
                    subdomain = document.Summary.Subdomain,
                    content = document.Content,
                    truncated = document.Truncated,
                }, Json);
            }
            default:
                throw new InvalidOperationException($"\"{name}\" is not a server tool.");
        }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
