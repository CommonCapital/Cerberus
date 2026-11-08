using System.Text.RegularExpressions;

namespace Cerberus.Scanning;

/// <summary>A single regex-based detection rule applied per line.</summary>
public sealed record Rule(
    string Id,
    string Title,
    Severity Severity,
    string Category,
    string? Cwe,
    string Confidence,
    Regex Pattern);

/// <summary>
/// MVP rule set for Phase-1 passive scanning: secret detection and a few
/// common insecure-code patterns. Language-agnostic, regex-based. This is the
/// deterministic layer; LLM reasoning over the results comes later.
/// </summary>
public static class Rules
{
    private static Regex R(string pattern) =>
        new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static readonly IReadOnlyList<Rule> All =
    [
        // --- Secrets (CWE-798: hardcoded credentials) ---
        new("secret.aws-access-key", "AWS access key id", Severity.High, "secret", "CWE-798", "high",
            R(@"\bAKIA[0-9A-Z]{16}\b")),
        new("secret.private-key", "Private key material", Severity.Critical, "secret", "CWE-798", "high",
            R(@"-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----")),
        new("secret.github-token", "GitHub personal access token", Severity.High, "secret", "CWE-798", "high",
            R(@"\bghp_[0-9A-Za-z]{36}\b")),
        new("secret.slack-token", "Slack token", Severity.High, "secret", "CWE-798", "high",
            R(@"\bxox[baprs]-[0-9A-Za-z-]{10,}")),
        new("secret.generic-assignment", "Hardcoded credential assignment", Severity.Medium, "secret", "CWE-798", "medium",
            R(@"(?i)\b(?:api[_-]?key|secret|token|passwd|password)\b\s*[:=]\s*[""'][A-Za-z0-9_\-]{12,}[""']")),

        // --- Insecure code patterns ---
        new("crypto.weak-hash", "Weak hash algorithm (MD5/SHA-1)", Severity.Medium, "crypto", "CWE-327", "low",
            R(@"(?i)\b(?:md5|sha1)\b")),
        new("tls.verification-disabled", "TLS/certificate verification disabled", Severity.High, "config", "CWE-295", "medium",
            R(@"(?i)(?:verify\s*=\s*False|rejectUnauthorized\s*:\s*false|InsecureSkipVerify\s*[:=]\s*true|CURLOPT_SSL_VERIFYPEER\s*,\s*0)")),
        new("injection.eval", "Use of eval / dynamic code execution", Severity.Medium, "injection", "CWE-95", "low",
            R(@"(?<![A-Za-z0-9_])eval\s*\(")),
        new("injection.shell-true", "Shell execution with shell=True", Severity.High, "injection", "CWE-78", "medium",
            R(@"(?i)subprocess\.(?:call|run|Popen|check_output)\([^)]*shell\s*=\s*True")),
    ];
}
