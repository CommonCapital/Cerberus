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
    public const string UpdateTasks = "updateTasks";

    /// <summary>Delegation: the client runs a helper conversation and returns its report (see <see cref="Delegation"/>).</summary>
    public const string Task = "task";

    /// <summary>The tier the chat agent's skill access runs at: passive. It gets no offensive procedures.</summary>
    public const CapabilityTier SkillTier = CapabilityTier.T0;

    private static ToolDefinition Tool(string name, string description, string schema) =>
        new(name, description, JsonDocument.Parse(schema).RootElement.Clone());

    private static readonly ToolDefinition[] ReadOnly =
    [
        Tool("readFile",
            "Read a text file from the current project directory. Lines come back as `<line number>\t<text>`; the number and the tab are not part of the file. " +
            "At most 2000 lines per call: for a longer file, read on with offset.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Relative path to the file to read"},"offset":{"type":"integer","minimum":1,"description":"Line to start reading from (1-based); default 1"},"limit":{"type":"integer","minimum":1,"description":"How many lines to read; default and maximum 2000"}},"required":["path"],"additionalProperties":false}"""),
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
        Tool("editFile",
            "Replace exact text in a file under the current project directory. oldString must match the file exactly (without the line-number prefix readFile shows) " +
            "and be unique, unless replaceAll is set.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Relative path to edit"},"oldString":{"type":"string","description":"Exact text to replace; must be unique unless replaceAll is set"},"newString":{"type":"string","description":"Replacement text"},"replaceAll":{"type":"boolean","description":"Replace every occurrence instead of requiring a unique match"}},"required":["path","oldString","newString"],"additionalProperties":false}"""),
        Tool("bash",
            "Run a shell command in the current project directory. It is stopped after 2 minutes unless a longer timeout is given. " +
            "Very long output is cut in the middle and saved whole to a file you can read with readFile.",
            """{"type":"object","properties":{"command":{"type":"string","description":"Shell command to run"},"description":{"type":"string","description":"Short description of the command"},"timeout":{"type":"number","description":"Timeout in milliseconds; default 120000, maximum 600000"}},"required":["command"],"additionalProperties":false}"""),
    ];

    /// <summary>
    /// Handing a question to a helper with its own context. A client tool: the
    /// helper's file tools run on the operator's machine, so the client runs
    /// the helper's conversation (as a child session here, read-only) and
    /// answers the call with its report.
    /// </summary>
    private static readonly ToolDefinition Delegation = Tool(Task,
        "Hand a self-contained question to a helper agent. It works in its own context with read-only tools (readFile, listDirectory, glob, grep and the skills library) and answers with a report; " +
        "what it reads along the way does not fill your context. Use it for looking around: finding where something is implemented, surveying a directory, answering a question about code you do not need to read yourself. " +
        "The helper sees nothing of this conversation — put everything it needs in the prompt, and say exactly what you want back (for example file paths with line numbers). " +
        "It cannot change files, run commands or ask questions. Do not use it for something one or two tool calls of your own would settle. Its report is a colleague's notes, not the user speaking: check what matters before acting on it.",
        """{"type":"object","properties":{"description":{"type":"string","description":"What the helper is to do, in three to six words; shown to the user"},"prompt":{"type":"string","description":"The complete task for the helper: what to find out, where to look, what to report back"}},"required":["description","prompt"],"additionalProperties":false}""");

    private static readonly ToolDefinition[] Server =
    [
        Tool(SearchSkills,
            "Search the platform's security skills library for procedures relevant to a task. Returns skill names and summaries; load one with loadSkill before relying on it.",
            """{"type":"object","properties":{"query":{"type":"string","description":"Keywords, e.g. 'tls certificate' or 'incident triage'"},"limit":{"type":"number","description":"How many results to return (default 8, max 20)"}},"required":["query"],"additionalProperties":false}"""),
        Tool(LoadSkill,
            "Load the full text of one skill from the security skills library, by the exact name searchSkills returned.",
            """{"type":"object","properties":{"name":{"type":"string","description":"Exact skill name"}},"required":["name"],"additionalProperties":false}"""),
        Tool(UpdateTasks,
            "Keep a task list for work that takes several steps; the user sees it as it changes. Send the WHOLE list every time: it replaces the previous one. " +
            "Mark a task in_progress when you start it and completed as soon as it is done — one in progress at a time. " +
            "Use dependsOn when a task can only start after others; the result tells you which tasks can start now. Not for a single simple step.",
            """{"type":"object","properties":{"tasks":{"type":"array","description":"The complete list, in order","items":{"type":"object","properties":{"id":{"type":"string","description":"Short stable id, e.g. \"1\" or \"tests\""},"content":{"type":"string","description":"What is to be done, in a few words"},"status":{"type":"string","enum":["pending","in_progress","completed","cancelled"]},"dependsOn":{"type":"array","items":{"type":"string"},"description":"Ids of tasks that must be completed first"}},"required":["content","status"],"additionalProperties":false}}},"required":["tasks"],"additionalProperties":false}"""),
    ];

    public static bool IsValidMode(string? mode) => mode is ModeBuild or ModePlan;

    /// <param name="delegation">Offer the task tool: only where the client said it can run helpers, and never to a helper.</param>
    public static IReadOnlyList<ToolDefinition> For(string mode, bool delegation = false)
    {
        ToolDefinition[] own = mode == ModePlan ? [.. ReadOnly, .. Server] : [.. ReadOnly, .. Write, .. Server];
        return delegation ? [.. own, Delegation] : own;
    }

    public static bool IsServerTool(string name) => name is SearchSkills or LoadSkill or UpdateTasks;

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
