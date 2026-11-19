using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Cerberus.Scanning;

/// <summary>A line a rule could not finish checking, even when tried again with a longer limit.</summary>
/// <param name="File">Relative to the scan root, with forward slashes.</param>
public sealed record UncheckedLine(string File, int Line, string RuleId);

/// <summary>
/// A rule's pattern runs under a short time limit, so one pathological line
/// cannot stall a scan. On a busy machine an ordinary line can run out of that
/// time too, and a line that is passed over silently is a finding that may be
/// missing without anyone knowing. So a match that times out is tried once
/// more with a longer limit; only a line that runs out of that as well is given
/// up — and the caller records it as not checked.
/// </summary>
public static class PatientMatch
{
    /// <summary>The limit of the second try.</summary>
    public static readonly TimeSpan SecondTry = TimeSpan.FromSeconds(2);

    private static readonly ConcurrentDictionary<Regex, Regex> Patient = new();

    /// <summary>Whether the pattern matches; null when it could not be decided in time, even on the second try.</summary>
    public static bool? IsMatch(Regex pattern, string input)
    {
        try
        {
            return pattern.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            try
            {
                return PatientFor(pattern).IsMatch(input);
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }
    }

    /// <summary>Every match, evaluated here; null when the search could not finish in time, even on the second try.</summary>
    public static IReadOnlyList<Match>? Matches(Regex pattern, string input)
    {
        try
        {
            return pattern.Matches(input).ToList();   // the search runs while the list is made
        }
        catch (RegexMatchTimeoutException)
        {
            try
            {
                return PatientFor(pattern).Matches(input).ToList();
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }
    }

    /// <summary>The same pattern and options with the longer limit, made once per pattern.</summary>
    private static Regex PatientFor(Regex pattern) =>
        Patient.GetOrAdd(pattern, p => new Regex(p.ToString(), p.Options, SecondTry));
}
