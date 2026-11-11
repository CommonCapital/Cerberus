using Cerberus.Authorization;

namespace Cerberus.Skills;

/// <summary>What is known about a skill without loading its text.</summary>
public sealed record SkillSummary(
    string Name,
    string Description,
    string Subdomain,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> MitreAttack,
    IReadOnlyList<string> NistCsf,
    /// <summary>The lowest capability tier an agent needs before it may load this skill.</summary>
    CapabilityTier MinimumTier);

public sealed record SkillDocument(SkillSummary Summary, string Content, bool Truncated);

public sealed record SkillOptions(string SkillsRoot);

/// <summary>
/// Which skills an agent may load, by capability tier (PLAN.md §3, §7.5).
/// Skills that describe how to attack a system are procedures for the
/// intrusive tiers; an agent working at a passive tier has no use for them and
/// must not receive them. The rule is deliberately coarse — by the library's
/// own subdomain label — and errs towards withholding.
/// </summary>
public static class SkillAccess
{
    private static readonly HashSet<string> OffensiveSubdomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "red-teaming", "red-team", "penetration-testing", "offensive-security",
    };

    public static CapabilityTier MinimumTierFor(string subdomain) =>
        OffensiveSubdomains.Contains(subdomain) ? CapabilityTier.T2 : CapabilityTier.T0;

    public static bool Allows(CapabilityTier agentTier, SkillSummary skill) => agentTier >= skill.MinimumTier;
}

/// <summary>
/// The vendored skills library (<c>security-skills/skills/&lt;name&gt;/SKILL.md</c>):
/// an index of every skill's metadata, a keyword search over it, and loading
/// of one skill's text by name. This is how agents get at the library — they
/// search for what fits the task, then load only that.
/// </summary>
public sealed class SkillLibrary
{
    public const int MaxContentLength = 24_000;

    private readonly string _root;
    private readonly Lazy<Dictionary<string, (SkillSummary Summary, string File)>> _index;

    public SkillLibrary(SkillOptions options)
    {
        _root = Path.GetFullPath(options.SkillsRoot);
        _index = new Lazy<Dictionary<string, (SkillSummary, string)>>(Load);
    }

    public int Count => _index.Value.Count;

    public IEnumerable<SkillSummary> All => _index.Value.Values.Select(v => v.Summary);

    public SkillSummary? Find(string name) =>
        _index.Value.TryGetValue(name, out var entry) ? entry.Summary : null;

    /// <summary>
    /// Skills matching the query that an agent of the given tier may load, best
    /// match first. Words are matched against name, tags, subdomain and
    /// description; a skill must match every word.
    /// </summary>
    public IReadOnlyList<SkillSummary> Search(
        string? query, CapabilityTier agentTier, int limit = 10, string? subdomain = null)
    {
        var words = (query ?? string.Empty)
            .Split([' ', ',', '/', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();

        return All
            .Where(s => SkillAccess.Allows(agentTier, s))
            .Where(s => subdomain is null || s.Subdomain.Equals(subdomain, StringComparison.OrdinalIgnoreCase))
            .Select(s => (Skill: s, Score: Score(s, words)))
            .Where(x => words.Count == 0 || x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Skill.Name, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(x => x.Skill)
            .ToList();
    }

    /// <summary>
    /// The skill's text, if it exists and the agent's tier allows it. The name
    /// is looked up in the index, never used as a path.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The skill needs a higher tier.</exception>
    public SkillDocument? Read(string name, CapabilityTier agentTier)
    {
        if (!_index.Value.TryGetValue(name, out var entry)) return null;
        if (!SkillAccess.Allows(agentTier, entry.Summary))
        {
            throw new UnauthorizedAccessException(
                $"Skill \"{entry.Summary.Name}\" needs capability tier {entry.Summary.MinimumTier}; this agent runs at {agentTier}.");
        }

        string text;
        try
        {
            text = File.ReadAllText(entry.File);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var body = SkillFrontmatter.Body(text).Trim();
        var truncated = body.Length > MaxContentLength;
        return new SkillDocument(entry.Summary, truncated ? body[..MaxContentLength] : body, truncated);
    }

    private static int Score(SkillSummary skill, List<string> words)
    {
        if (words.Count == 0) return 1;
        var name = skill.Name.ToLowerInvariant();
        var description = skill.Description.ToLowerInvariant();
        var total = 0;

        foreach (var word in words)
        {
            var score = 0;
            if (name.Contains(word)) score += 5;
            if (skill.Tags.Any(t => t.Contains(word, StringComparison.OrdinalIgnoreCase))) score += 3;
            if (skill.Subdomain.Contains(word, StringComparison.OrdinalIgnoreCase)) score += 2;
            if (description.Contains(word)) score += 1;
            if (score == 0) return 0;   // every word must match somewhere
            total += score;
        }
        return total;
    }

    private Dictionary<string, (SkillSummary, string)> Load()
    {
        var index = new Dictionary<string, (SkillSummary, string)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_root)) return index;

        foreach (var directory in Directory.GetDirectories(_root).Order(StringComparer.Ordinal))
        {
            var file = Path.Combine(directory, "SKILL.md");
            // A link could point outside the library; only real files inside it are indexed.
            if (!File.Exists(file) || new FileInfo(file).LinkTarget is not null) continue;

            try
            {
                var fields = SkillFrontmatter.Parse(File.ReadAllText(file));
                var name = fields.Scalar("name") ?? Path.GetFileName(directory);
                var subdomain = fields.Scalar("subdomain") ?? "general";
                index[name] = (new SkillSummary(
                    name,
                    fields.Scalar("description") ?? string.Empty,
                    subdomain,
                    fields.List("tags"),
                    fields.List("mitre_attack"),
                    fields.List("nist_csf"),
                    SkillAccess.MinimumTierFor(subdomain)), file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable skill: skip it rather than fail the whole library.
            }
        }
        return index;
    }
}

/// <summary>Reads the YAML front matter of a SKILL.md: scalars, folded scalars and simple lists.</summary>
public sealed class SkillFrontmatter
{
    private readonly Dictionary<string, List<string>> _fields = new(StringComparer.OrdinalIgnoreCase);

    public string? Scalar(string key) =>
        _fields.TryGetValue(key, out var values) && values.Count > 0 ? string.Join(" ", values) : null;

    public IReadOnlyList<string> List(string key) =>
        _fields.TryGetValue(key, out var values) ? values : [];

    public static SkillFrontmatter Parse(string text)
    {
        var result = new SkillFrontmatter();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return result;

        string? key = null;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() == "---") break;
            if (line.Trim().Length == 0) continue;

            if (!char.IsWhiteSpace(line[0]) && !line.StartsWith('-'))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                key = line[..colon].Trim();
                var value = Clean(line[(colon + 1)..]);
                result._fields[key] = [];
                // ">" and "|" introduce a block scalar whose text is on the following lines.
                if (value.Length > 0 && value is not (">" or "|" or ">-" or "|-")) result._fields[key].Add(value);
            }
            else if (key is not null)
            {
                var item = line.Trim();
                if (item.StartsWith("- ")) item = item[2..];
                item = Clean(item);
                if (item.Length > 0) result._fields[key].Add(item);
            }
        }
        return result;
    }

    /// <summary>The document without its front matter.</summary>
    public static string Body(string text)
    {
        text = text.Replace("\r\n", "\n");
        if (!text.StartsWith("---\n")) return text;
        var end = text.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        return end < 0 ? text : text[(end + 5)..];
    }

    private static string Clean(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }
        return value;
    }
}
