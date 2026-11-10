namespace Cerberus.Authorization;

/// <summary>Capability tiers (PLAN.md §3). Higher tiers need stronger gates.</summary>
public enum CapabilityTier
{
    /// <summary>Passive: SAST, SCA, secrets, compliance read.</summary>
    T0 = 0,

    /// <summary>Active-safe: light probing, unauthenticated recon, config checks.</summary>
    T1 = 1,

    /// <summary>Active-intrusive: exploit attempts, fuzzing, payload delivery.</summary>
    T2 = 2,

    /// <summary>Destructive: fault injection, load/flood testing.</summary>
    T3 = 3,
}

public enum EngagementStatus
{
    Active,

    /// <summary>Finished normally. No further runs.</summary>
    Closed,

    /// <summary>Stopped by the operator's kill switch. No further runs.</summary>
    Aborted,
}

public enum TargetType
{
    /// <summary>A directory or file under the scan root (code target).</summary>
    Path,
    Host,
    Domain,
}

public enum VerificationStatus
{
    /// <summary>No proof of control yet. Passive tiers only.</summary>
    Pending,

    /// <summary>The operator attested they own or were supplied this code target.</summary>
    Attested,

    /// <summary>A proof-of-control challenge passed (not implemented yet).</summary>
    Verified,
}

/// <summary>The scope-relevant facts of an engagement, detached from storage.</summary>
public sealed record EngagementScope(
    EngagementStatus Status,
    CapabilityTier MaxTier,
    DateTime? StartsAt,
    DateTime? EndsAt,
    IReadOnlyList<ScopedTarget> Targets);

public sealed record ScopedTarget(TargetType Type, string Locator, VerificationStatus Verification);

public sealed record ScopeDecision(bool Allowed, string Reason)
{
    public static ScopeDecision Allow(string reason) => new(true, reason);

    public static ScopeDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// The dispatch-time gate: decides whether a run of a given tier against a
/// given target is inside an engagement's scope. Enforced by the platform
/// before any work starts — never left to an agent's judgement.
/// </summary>
public static class ScopePolicy
{
    /// <summary>
    /// The highest tier an engagement may be created with today. T2/T3 need
    /// proof-of-control verification and an explicit authorization step, which
    /// do not exist yet, so they cannot be granted at all.
    /// </summary>
    public const CapabilityTier MaxGrantableTier = CapabilityTier.T1;

    public static ScopeDecision Check(
        EngagementScope engagement, CapabilityTier tier, TargetType targetType, string locator, DateTime now)
    {
        if (engagement.Status != EngagementStatus.Active)
        {
            return ScopeDecision.Deny($"Engagement is {engagement.Status.ToString().ToLowerInvariant()}; no further runs are allowed.");
        }
        if (engagement.StartsAt is { } start && now < start)
        {
            return ScopeDecision.Deny($"Engagement window has not opened yet (starts {start:u}).");
        }
        if (engagement.EndsAt is { } end && now >= end)
        {
            return ScopeDecision.Deny($"Engagement window has ended ({end:u}).");
        }
        if (tier > engagement.MaxTier)
        {
            return ScopeDecision.Deny($"Tier {tier} exceeds the engagement's maximum tier {engagement.MaxTier}.");
        }
        if (tier > MaxGrantableTier)
        {
            return ScopeDecision.Deny($"Tier {tier} is not available: ownership verification and explicit authorization are not implemented.");
        }

        var normalized = Normalize(targetType, locator);
        if (normalized is null)
        {
            return ScopeDecision.Deny($"\"{locator}\" is not a valid {targetType.ToString().ToLowerInvariant()} target.");
        }

        var match = engagement.Targets.FirstOrDefault(t => t.Type == targetType && Covers(t, normalized));
        if (match is null)
        {
            return ScopeDecision.Deny($"Target \"{locator}\" is outside the engagement's scope.");
        }

        return ScopeDecision.Allow($"In scope of {match.Type.ToString().ToLowerInvariant()} target \"{Display(match)}\".");
    }

    /// <summary>
    /// Canonical form of a target locator, or null if it is not acceptable.
    /// Paths are relative to the scan root, use '/' separators, and may not
    /// escape upwards; the scan root itself is the empty string.
    /// </summary>
    public static string? Normalize(TargetType type, string? locator)
    {
        locator = locator?.Trim() ?? string.Empty;
        return type == TargetType.Path ? NormalizePath(locator) : NormalizeHost(locator);
    }

    private static string? NormalizePath(string path)
    {
        if (path.StartsWith('/') || path.StartsWith('\\') || path.StartsWith('~') ||
            (path.Length >= 2 && path[1] == ':') || path.Contains('\0'))
        {
            return null;   // absolute or otherwise outside the scan root
        }

        var parts = new List<string>();
        foreach (var part in path.Split('/', '\\'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") return null;   // never resolve upwards, even if it would stay inside
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static string? NormalizeHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        if (host.Length is 0 or > 253) return null;
        return host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':') ? host : null;
    }

    private static bool Covers(ScopedTarget target, string normalized)
    {
        var scope = Normalize(target.Type, target.Locator);
        if (scope is null) return false;

        return target.Type switch
        {
            // A path target covers itself and everything beneath it.
            TargetType.Path => scope.Length == 0 || normalized == scope ||
                               normalized.StartsWith(scope + "/", StringComparison.Ordinal),
            // A domain target covers itself and its subdomains.
            TargetType.Domain => normalized == scope || normalized.EndsWith("." + scope, StringComparison.Ordinal),
            _ => normalized == scope,
        };
    }

    private static string Display(ScopedTarget target)
    {
        var normalized = Normalize(target.Type, target.Locator) ?? target.Locator;
        return target.Type == TargetType.Path && normalized.Length == 0 ? "." : normalized;
    }
}
