namespace Cerberus.Scanning;

/// <summary>
/// Orders version strings well enough to choose a fix: numeric parts compared
/// as numbers, a pre-release below its release, a leading "v" and build
/// metadata ignored. It is not any one ecosystem's full rule set (PEP 440
/// epochs, Maven qualifiers) — where two versions cannot be told apart
/// numerically they are compared as text, and callers fall back safely.
/// </summary>
public static class VersionOrder
{
    public static int Compare(string a, string b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);

        var partsA = coreA.Split('.');
        var partsB = coreB.Split('.');
        for (var i = 0; i < Math.Max(partsA.Length, partsB.Length); i++)
        {
            var c = ComparePart(i < partsA.Length ? partsA[i] : "0", i < partsB.Length ? partsB[i] : "0");
            if (c != 0) return c;
        }

        // 1.2.0-rc1 comes before 1.2.0.
        if (preA is null || preB is null) return preA is null ? (preB is null ? 0 : 1) : -1;

        var idsA = preA.Split('.', '-');
        var idsB = preB.Split('.', '-');
        for (var i = 0; i < Math.Min(idsA.Length, idsB.Length); i++)
        {
            var c = ComparePart(idsA[i], idsB[i]);
            if (c != 0) return c;
        }
        return idsA.Length.CompareTo(idsB.Length);
    }

    /// <summary>
    /// The fix to move to from <paramref name="current"/>: the lowest fixed
    /// version above it. An advisory for a library maintained on several
    /// branches lists one fix per branch; the first one listed may be on an
    /// older branch than the one in use. Null when no listed fix is above the
    /// current version — then none of them is advice worth giving.
    /// </summary>
    public static string? NextFix(string current, IReadOnlyList<string>? fixedVersions)
    {
        if (fixedVersions is null) return null;
        string? best = null;
        foreach (var candidate in fixedVersions)
        {
            if (string.IsNullOrWhiteSpace(candidate) || Compare(candidate, current) <= 0) continue;
            if (best is null || Compare(candidate, best) < 0) best = candidate;
        }
        return best;
    }

    private static (string Core, string? PreRelease) Split(string version)
    {
        version = version.Trim();
        if (version.Length > 1 && version[0] is 'v' or 'V' && char.IsAsciiDigit(version[1])) version = version[1..];
        var plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        var dash = version.IndexOf('-');
        return dash < 0 ? (version, null) : (version[..dash], version[(dash + 1)..]);
    }

    private static int ComparePart(string a, string b)
    {
        var numericA = ulong.TryParse(a, out var na);
        var numericB = ulong.TryParse(b, out var nb);
        if (numericA && numericB) return na.CompareTo(nb);
        if (numericA != numericB) return numericA ? -1 : 1;   // as in semver: a number sorts before a word
        return string.CompareOrdinal(a, b);
    }
}
