namespace Cerberus.Authorization;

/// <summary>
/// Classes of action an agent or run can take that the operator may want to
/// be asked about (PLAN.md §6.5, permission modes).
/// </summary>
public static class ActionKinds
{
    /// <summary>Reading files of the project (chat tools: readFile, listDirectory, glob, grep).</summary>
    public const string ReadFiles = "read-files";

    /// <summary>Creating or changing files (chat tools: writeFile, editFile).</summary>
    public const string EditFiles = "edit-files";

    /// <summary>Running shell commands (chat tool: bash).</summary>
    public const string RunCommands = "run-commands";

    /// <summary>Starting a background job (scan, assessment, triage).</summary>
    public const string RunJob = "run-job";

    /// <summary>Sending source code to a model provider outside this machine.</summary>
    public const string SendCodeToCloudModel = "send-code-to-cloud-model";

    public static readonly IReadOnlyList<(string Kind, string Description)> All =
    [
        (ReadFiles, "Read files of the project"),
        (EditFiles, "Create or change files"),
        (RunCommands, "Run shell commands"),
        (RunJob, "Start background jobs (scans, assessments, triage)"),
        (SendCodeToCloudModel, "Send source code to a cloud model provider"),
    ];

    public static bool IsValid(string? kind) => All.Any(k => k.Kind == kind);
}

public enum PermissionMode
{
    /// <summary>Passive work runs without asking; anything that changes things or leaves the machine asks.</summary>
    Default,

    /// <summary>Everything asks, including reading files and starting passive jobs.</summary>
    Ask,
}

/// <summary>
/// How much may happen without asking: a mode, plus the action kinds the
/// operator has said never to ask about again ("always allow").
/// </summary>
public sealed record PermissionPolicy(PermissionMode Mode, IReadOnlySet<string> AllowedKinds)
{
    public static PermissionPolicy Standard { get; } = new(PermissionMode.Default, new HashSet<string>());

    /// <summary>
    /// Whether an action of this kind must wait for the operator. An "always
    /// allow" for the kind wins in either mode — it is an explicit decision.
    /// Otherwise reading and starting passive jobs ask only in Ask mode;
    /// editing, running commands and sending code out always ask.
    /// </summary>
    public bool NeedsApproval(string kind)
    {
        if (AllowedKinds.Contains(kind)) return false;
        return kind is ActionKinds.ReadFiles or ActionKinds.RunJob ? Mode == PermissionMode.Ask : true;
    }

    /// <summary>Of the kinds an action involves, those the operator must still approve.</summary>
    public IReadOnlyList<string> Unapproved(IEnumerable<string> kinds) => kinds.Where(NeedsApproval).ToList();
}
