namespace Cerberus.Scanning;

public enum Severity
{
    Info,
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>A normalized security finding (the Phase-1 output schema).</summary>
public sealed record Finding(
    string Id,
    string Title,
    Severity Severity,
    string Category,
    string? Cwe,
    string File,
    int Line,
    string Evidence,
    string Confidence,
    // --- Optional SCA (dependency) context; null for static-analysis findings ---
    string? Package = null,
    string? Version = null,
    string? FixedVersion = null,
    string? VulnId = null,
    string? Reference = null,
    // --- Optional static-analysis context ---
    /// <summary>OWASP Top 10 (2021) category, e.g. "A03".</summary>
    string? Owasp = null,
    /// <summary>A short hint on how to fix it.</summary>
    string? Remediation = null,
    /// <summary>"test" or "docs" when the file is test code or documentation; confidence is lowered there.</summary>
    string? Context = null,
    /// <summary>
    /// True when the target itself marked this line with a <c>cerberus:ignore</c>
    /// comment. Suppressed findings stay in the results (the target must not be
    /// able to hide them) but do not count towards totals or compliance.
    /// </summary>
    bool Suppressed = false);
