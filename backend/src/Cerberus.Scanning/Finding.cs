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
    string Confidence);
