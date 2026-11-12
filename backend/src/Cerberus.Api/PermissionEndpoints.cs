using System.Text.Json;
using System.Text.Json.Serialization;
using Cerberus.Api.Audit;
using Cerberus.Api.Auth;
using Cerberus.Authorization;
using Cerberus.Data;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api;

public sealed record PermissionsRequest(string? Mode, List<string>? AllowedKinds, string? EngagementId, List<string>? AllowedCommands = null);

/// <summary>
/// Command rules: prefixes of shell commands ("git status", "npm test") that the
/// chat agent may run without asking. The CLI decides whether a command line is
/// covered — it is the one that runs it; the server stores the rules and refuses
/// ones that could never be a safe blanket permission.
/// </summary>
public static class CommandRules
{
    public const int MaxRules = 200;
    public const int MaxLength = 200;

    /// <summary>
    /// Commands that run other commands or code, reach the network, or destroy
    /// things: "always allow" for one of these would be "always allow anything".
    /// The CLI has the same list and never offers them; this is the second lock.
    /// </summary>
    public static readonly IReadOnlySet<string> NeverRemembered = new HashSet<string>(StringComparer.Ordinal)
    {
        "rm", "rmdir", "sudo", "su", "doas", "dd", "mkfs", "shred", "truncate", "shutdown", "reboot", "halt",
        "kill", "killall", "pkill", "eval", "exec", "source", ".", "sh", "bash", "zsh", "fish", "dash", "ksh",
        "python", "python2", "python3", "node", "ruby", "perl", "php", "deno", "npx", "bunx", "osascript", "pwsh", "powershell",
        "curl", "wget", "ssh", "scp", "sftp", "nc", "ncat", "telnet", "ftp",
        "find", "xargs", "env", "nohup", "watch", "awk", "gawk", "sed", "tee",
        "chmod", "chown", "chgrp", "ln", "crontab", "launchctl", "systemctl", "service", "mount", "umount", "diskutil",
    };

    /// <summary>The rule in its stored form (single spaces), or why it cannot be one.</summary>
    public static (string? Rule, string? Error) Normalize(string? rule)
    {
        var tokens = (rule ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return (null, "A command rule cannot be empty.");
        var text = string.Join(' ', tokens);
        if (text.Length > MaxLength) return (null, $"A command rule is at most {MaxLength} characters.");
        // A rule is plain words: anything the shell would interpret has no place in it.
        if (text.IndexOfAny(['|', '&', ';', '<', '>', '$', '`', '(', ')', '{', '}', '"', '\'', '\\', '\n', '*', '?', '~', '!', '=']) >= 0)
        {
            return (null, $"\"{text}\" is not a plain command prefix.");
        }
        var name = tokens[0].Contains('/') ? tokens[0][(tokens[0].LastIndexOf('/') + 1)..] : tokens[0];
        if (NeverRemembered.Contains(name)) return (null, $"\"{tokens[0]}\" can run or destroy anything; it is always asked about.");
        return (text, null);
    }

    public static List<string> Read(string? json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(string.IsNullOrWhiteSpace(json) ? "[]" : json) ?? [])
                .Select(r => Normalize(r).Rule).Where(r => r is not null).Select(r => r!).Distinct().ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>
/// Permission modes (PLAN.md §6.5): how much runs without asking. A user has a
/// default policy; an engagement may override it. The CLI reads the same
/// policy to decide which chat tool calls to ask about.
/// </summary>
public static class PermissionEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void MapPermissionEndpoints(this WebApplication app)
    {
        var permissions = app.MapGroup("/settings/permissions").AddEndpointFilter<RequireAuthFilter>();
        permissions.MapGet("/", Get);
        permissions.MapPut("/", Put);
        permissions.MapDelete("/", Delete);
    }

    /// <summary>The policy that applies: the engagement's own if it has one, else the user's, else the standard one.</summary>
    public static async Task<(PermissionPolicy Policy, string Scope)> ResolveAsync(
        CerberusDbContext db, string userId, string? engagementId, CancellationToken ct)
    {
        var settings = await db.PermissionSettings.AsNoTracking()
            .Where(p => p.UserId == userId && (p.EngagementId == null || p.EngagementId == engagementId))
            .ToListAsync(ct);

        var own = engagementId is null ? null : settings.FirstOrDefault(p => p.EngagementId == engagementId);
        var user = settings.FirstOrDefault(p => p.EngagementId == null);
        var chosen = own ?? user;
        return chosen is null
            ? (PermissionPolicy.Standard, "standard")
            : (ToPolicy(chosen), own is not null ? "engagement" : "user");
    }

    /// <summary>Adds kinds to the "always allow" list of the engagement (if given) or the user.</summary>
    public static async Task AllowAsync(
        CerberusDbContext db, string userId, string? engagementId, IEnumerable<string> kinds, CancellationToken ct)
    {
        var setting = await db.PermissionSettings
            .FirstOrDefaultAsync(p => p.UserId == userId && p.EngagementId == engagementId, ct);
        if (setting is null)
        {
            // A new engagement override starts from what applied until now, so it only ever adds.
            var (inherited, _) = await ResolveAsync(db, userId, engagementId, ct);
            setting = new PermissionSetting
            {
                UserId = userId,
                EngagementId = engagementId,
                Mode = inherited.Mode.ToString(),
                AllowedKinds = JsonSerializer.Serialize(inherited.AllowedKinds),
            };
            db.PermissionSettings.Add(setting);
        }

        var allowed = ReadKinds(setting.AllowedKinds);
        allowed.UnionWith(kinds.Where(ActionKinds.IsValid));
        setting.AllowedKinds = JsonSerializer.Serialize(allowed.Order());
        setting.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static async Task<IResult> Get(HttpContext http, CerberusDbContext db, string? engagementId, CancellationToken ct)
    {
        var userId = UserId(http);
        if (await CheckEngagementAsync(db, userId, engagementId, ct) is { } problem) return problem;

        var (policy, scope) = await ResolveAsync(db, userId, Normalize(engagementId), ct);
        return Results.Json(Describe(policy, scope, await CommandsAsync(db, userId, Normalize(engagementId), ct)), JsonOptions);
    }

    /// <summary>The command rules that apply: the engagement's own setting if it has one, else the user's.</summary>
    private static async Task<List<string>> CommandsAsync(CerberusDbContext db, string userId, string? engagementId, CancellationToken ct)
    {
        var settings = await db.PermissionSettings.AsNoTracking()
            .Where(p => p.UserId == userId && (p.EngagementId == null || p.EngagementId == engagementId))
            .ToListAsync(ct);
        var chosen = (engagementId is null ? null : settings.FirstOrDefault(p => p.EngagementId == engagementId))
                     ?? settings.FirstOrDefault(p => p.EngagementId == null);
        return CommandRules.Read(chosen?.AllowedCommands);
    }

    private static async Task<IResult> Put(
        PermissionsRequest body, HttpContext http, CerberusDbContext db, AuditLog audit, CancellationToken ct)
    {
        var userId = UserId(http);
        var engagementId = Normalize(body.EngagementId);
        if (await CheckEngagementAsync(db, userId, engagementId, ct) is { } problem) return problem;

        PermissionMode? mode = null;
        if (body.Mode is not null)
        {
            if (!Enum.TryParse<PermissionMode>(body.Mode, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return BadRequest("mode must be default or ask");
            }
            mode = parsed;
        }
        if (body.AllowedKinds?.FirstOrDefault(k => !ActionKinds.IsValid(k)) is { } unknown)
        {
            return BadRequest($"Unknown action kind \"{unknown}\". Known: {string.Join(", ", ActionKinds.All.Select(k => k.Kind))}");
        }
        List<string>? commands = null;
        if (body.AllowedCommands is not null)
        {
            if (body.AllowedCommands.Count > CommandRules.MaxRules) return BadRequest($"At most {CommandRules.MaxRules} command rules.");
            commands = [];
            foreach (var given in body.AllowedCommands)
            {
                var (rule, error) = CommandRules.Normalize(given);
                if (rule is null) return BadRequest(error!);
                if (!commands.Contains(rule)) commands.Add(rule);
            }
        }
        if (mode is null && body.AllowedKinds is null && commands is null) return BadRequest("Give mode, allowedKinds, allowedCommands, or several of them");

        var setting = await db.PermissionSettings
            .FirstOrDefaultAsync(p => p.UserId == userId && p.EngagementId == engagementId, ct);
        if (setting is null)
        {
            var (inherited, _) = await ResolveAsync(db, userId, engagementId, ct);
            setting = new PermissionSetting
            {
                UserId = userId,
                EngagementId = engagementId,
                Mode = inherited.Mode.ToString(),
                AllowedKinds = JsonSerializer.Serialize(inherited.AllowedKinds.Order()),
                AllowedCommands = JsonSerializer.Serialize(await CommandsAsync(db, userId, engagementId, ct)),
            };
            db.PermissionSettings.Add(setting);
        }
        if (mode is { } m) setting.Mode = m.ToString();
        if (body.AllowedKinds is not null) setting.AllowedKinds = JsonSerializer.Serialize(body.AllowedKinds.Distinct().Order());
        if (commands is not null) setting.AllowedCommands = JsonSerializer.Serialize(commands.Order(StringComparer.Ordinal));
        setting.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        var policy = ToPolicy(setting);
        await audit.AppendAsync(userId, "permissions.update", AuditOutcome.Ok,
            $"Permissions for {(engagementId is null ? "the user" : "the engagement")}: mode {policy.Mode.ToString().ToLowerInvariant()}, " +
            $"always allowed: {(policy.AllowedKinds.Count == 0 ? "nothing" : string.Join(", ", policy.AllowedKinds.Order()))}; " +
            $"commands run without asking: {(CommandRules.Read(setting.AllowedCommands) is { Count: > 0 } rules ? string.Join(", ", rules) : "none")}.",
            engagementId, ct: ct);

        return Results.Json(Describe(policy, engagementId is null ? "user" : "engagement", CommandRules.Read(setting.AllowedCommands)), JsonOptions);
    }

    /// <summary>Removes an override, falling back to the user's policy (or the standard one).</summary>
    private static async Task<IResult> Delete(
        HttpContext http, CerberusDbContext db, AuditLog audit, string? engagementId, CancellationToken ct)
    {
        var userId = UserId(http);
        var id = Normalize(engagementId);
        var removed = await db.PermissionSettings
            .Where(p => p.UserId == userId && p.EngagementId == id)
            .ExecuteDeleteAsync(ct);
        if (removed > 0)
        {
            await audit.AppendAsync(userId, "permissions.reset", AuditOutcome.Ok,
                $"Permissions for {(id is null ? "the user" : "the engagement")} reset.", id, ct: ct);
        }

        var (policy, scope) = await ResolveAsync(db, userId, id, ct);
        return Results.Json(Describe(policy, scope, await CommandsAsync(db, userId, id, ct)), JsonOptions);
    }

    private static async Task<IResult?> CheckEngagementAsync(
        CerberusDbContext db, string userId, string? engagementId, CancellationToken ct)
    {
        var id = Normalize(engagementId);
        if (id is null) return null;
        return await db.Engagements.AnyAsync(e => e.Id == id && e.UserId == userId, ct)
            ? null
            : Results.Json(new { error = "Engagement not found" }, statusCode: 404);
    }

    private static object Describe(PermissionPolicy policy, string scope, IReadOnlyList<string> commands) => new
    {
        mode = policy.Mode,
        allowedKinds = policy.AllowedKinds.Order(),
        // Shell commands the chat agent may run without asking, as prefixes ("git status").
        allowedCommands = commands,
        // "standard" = nothing stored; "user" = the user's default; "engagement" = this engagement's own.
        scope,
        kinds = ActionKinds.All.Select(k => new
        {
            kind = k.Kind,
            description = k.Description,
            asks = policy.NeedsApproval(k.Kind),
        }),
    };

    private static PermissionPolicy ToPolicy(PermissionSetting setting) => new(
        Enum.TryParse<PermissionMode>(setting.Mode, out var mode) ? mode : PermissionMode.Default,
        ReadKinds(setting.AllowedKinds));

    private static HashSet<string> ReadKinds(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<string>>(json) ?? []).Where(ActionKinds.IsValid).ToHashSet();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Normalize(string? engagementId) => string.IsNullOrWhiteSpace(engagementId) ? null : engagementId;

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static IResult BadRequest(string error) => Results.Json(new { error }, statusCode: 400);
}
