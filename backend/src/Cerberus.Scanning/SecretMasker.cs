using System.Text.RegularExpressions;

namespace Cerberus.Scanning;

/// <summary>
/// Replaces credential-shaped values in a line of code by a placeholder that
/// keeps only their length. Used wherever code is kept or passed on beyond
/// where it has to go: a finding's evidence, a run's stored events, an export,
/// a prompt for a cloud model. The secret itself stays in the file it was found in.
/// </summary>
public static class SecretMasker
{
    private static readonly IReadOnlyList<Rule> SecretRules = Rules.All.Where(r => r.Category == "secret").ToList();

    /// <summary>The line with everything the secret rules match masked.</summary>
    public static string Mask(string line)
    {
        foreach (var rule in SecretRules)
        {
            try
            {
                line = rule.Pattern.Replace(line, m => $"[REDACTED len={m.Length}]");
            }
            catch (RegexMatchTimeoutException)
            {
                return "[REDACTED line]";   // could not be checked, so nothing of it is kept
            }
        }
        return line;
    }

    /// <summary>The same, line by line, for a block of text.</summary>
    public static string MaskText(string text) =>
        text.IndexOf('\n') < 0 ? Mask(text) : string.Join('\n', text.Split('\n').Select(Mask));
}
