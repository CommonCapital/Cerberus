namespace Cerberus.Compliance;

/// <summary>
/// An operator's answer for one control, in the catalog's response schema
/// (verdict + justification + action plan + time limit). It takes precedence
/// over the automated verdict: the operator may complete a control the engine
/// could not judge, or overrule the scanner (e.g. a false positive).
/// </summary>
public sealed record OperatorResponse(
    string ControlId,
    string Verdict,
    string Justification,
    string? ActionPlan,
    int? TimeLimitDays,
    DateTime UpdatedAt);

/// <summary>What an operator submits for a control (unvalidated).</summary>
public sealed record OperatorResponseInput(
    string? Verdict,
    string? Justification,
    string? ActionPlan,
    int? TimeLimitDays);

/// <summary>Counts by effective verdict: the operator's answer if present, else the automated one.</summary>
public sealed record EffectiveSummary(
    int Compliant,
    int PartiallyCompliant,
    int NonCompliant,
    int NotApplicable,
    /// <summary>Controls with neither an automated verdict nor an operator answer.</summary>
    int Unanswered,
    int OperatorAnswered);

public static class OperatorResponses
{
    public static readonly IReadOnlyList<string> DefaultScale =
        ["Compliant", "Partially compliant", "Non-compliant", "Not applicable"];

    private const int FallbackMaxDays = 3650;
    private const int MaxTextLength = 10_000;

    public static IReadOnlyList<string> ScaleOf(Catalog? catalog) =>
        catalog?.ResponseScale is { Count: > 0 } scale ? scale : DefaultScale;

    /// <summary>
    /// Validates an operator answer against the catalog's response schema.
    /// Returns the normalized response, or an error message.
    /// </summary>
    public static (OperatorResponse? Response, string? Error) Validate(
        string controlId, OperatorResponseInput input, Catalog? catalog, DateTime now)
    {
        var scale = ScaleOf(catalog);
        var verdict = scale.FirstOrDefault(v => Normalize(v) == Normalize(input.Verdict));
        if (verdict is null)
        {
            return (null, $"Verdict must be one of: {string.Join(", ", scale)}.");
        }

        var justification = input.Justification?.Trim() ?? string.Empty;
        if (justification.Length == 0)
        {
            return (null, "A justification (detailed response) is required.");
        }

        var actionPlan = string.IsNullOrWhiteSpace(input.ActionPlan) ? null : input.ActionPlan.Trim();
        if (justification.Length > MaxTextLength || actionPlan?.Length > MaxTextLength)
        {
            return (null, $"Justification and action plan are limited to {MaxTextLength} characters each.");
        }

        // The response schema requires a remediation plan for anything short of compliant.
        var needsPlan = IsNonCompliant(verdict) || IsPartial(verdict);
        if (needsPlan && actionPlan is null)
        {
            return (null, $"An action plan is required when the verdict is \"{verdict}\".");
        }

        var maxDays = catalog?.Methodology?.CorrectiveActions?.MaxDays ?? FallbackMaxDays;
        if (input.TimeLimitDays is { } days && (days < 1 || days > maxDays))
        {
            return (null, $"Time limit must be between 1 and {maxDays} days.");
        }

        return (new OperatorResponse(controlId, verdict, justification, actionPlan, input.TimeLimitDays, now), null);
    }

    public static EffectiveSummary Summarize(
        IReadOnlyList<ControlResult> controls, IReadOnlyDictionary<string, OperatorResponse> responses)
    {
        int compliant = 0, partial = 0, nonCompliant = 0, notApplicable = 0, unanswered = 0, answered = 0;

        foreach (var control in controls)
        {
            if (responses.TryGetValue(control.ControlId, out var response))
            {
                answered++;
                if (IsNonCompliant(response.Verdict)) nonCompliant++;
                else if (IsPartial(response.Verdict)) partial++;
                else if (IsNotApplicable(response.Verdict)) notApplicable++;
                else compliant++;
                continue;
            }

            switch (control.Verdict)
            {
                case ControlVerdict.Compliant: compliant++; break;
                case ControlVerdict.NonCompliant: nonCompliant++; break;
                default: unanswered++; break;
            }
        }

        return new EffectiveSummary(compliant, partial, nonCompliant, notApplicable, unanswered, answered);
    }

    /// <summary>The response-scale wording for an automated verdict; empty when the engine could not judge.</summary>
    public static string AutomatedLabel(ControlVerdict verdict) => verdict switch
    {
        ControlVerdict.Compliant => "Compliant",
        ControlVerdict.NonCompliant => "Non-compliant",
        _ => string.Empty,
    };

    // Verdict wording varies slightly between catalogs ("Non-compliant", "NonCompliant").
    public static string Normalize(string? value) =>
        new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool IsNonCompliant(string verdict) => Normalize(verdict) == "noncompliant";

    private static bool IsPartial(string verdict) => Normalize(verdict).StartsWith("partial", StringComparison.Ordinal);

    private static bool IsNotApplicable(string verdict) => Normalize(verdict) is "notapplicable" or "na";
}
