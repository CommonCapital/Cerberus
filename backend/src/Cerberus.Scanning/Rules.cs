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
    Regex Pattern,
    string Remediation,
    /// <summary>File extensions (with dot) the rule does not apply to, where its pattern means something else.</summary>
    IReadOnlyList<string>? ExcludedExtensions = null)
{
    public bool AppliesTo(string extension) =>
        ExcludedExtensions is null || !ExcludedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Deterministic rule set for Phase-1 passive scanning: secrets, weak crypto,
/// insecure configuration, injection sinks and unsafe deserialization, across
/// the languages the platform meets most (Python, JS/TS, Java, C#, Go, shell,
/// and Erlang/Elixir for the RabbitMQ fixture). Line-based regexes: they find
/// candidates cheaply and explainably, not proofs — data-flow and context are
/// the job of the LLM layer on top (PLAN.md §9).
/// </summary>
public static class Rules
{
    /// <summary>Scanner finding categories. Compliance catalogs map controls onto these.</summary>
    public static readonly IReadOnlyList<string> Categories =
        ["secret", "crypto", "config", "injection", "deserialization"];

    /// <summary>OWASP Top 10 (2021) category for each scanner category.</summary>
    public static string? OwaspFor(string category) => category switch
    {
        "secret" => "A07",
        "crypto" => "A02",
        "config" => "A05",
        "injection" => "A03",
        "deserialization" => "A08",
        "dependency" => "A06",
        _ => null,
    };

    // A per-match timeout keeps a pathological line from stalling a scan.
    private static Regex R(string pattern) =>
        new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private const string UseSecretStore =
        "Remove the secret from the code, rotate it, and load it from a secret store or environment variable.";

    public static readonly IReadOnlyList<Rule> All =
    [
        // --- Secrets (CWE-798: hardcoded credentials) -------------------------
        new("secret.aws-access-key", "AWS access key id", Severity.High, "secret", "CWE-798", "high",
            R(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"), UseSecretStore),
        new("secret.private-key", "Private key material", Severity.Critical, "secret", "CWE-798", "high",
            R(@"-----BEGIN (?:RSA |EC |OPENSSH |DSA |PGP |ENCRYPTED )?PRIVATE KEY(?: BLOCK)?-----"), UseSecretStore),
        new("secret.github-token", "GitHub token", Severity.High, "secret", "CWE-798", "high",
            R(@"\b(?:gh[pousr]_[0-9A-Za-z]{36,}|github_pat_[0-9A-Za-z_]{40,})\b"), UseSecretStore),
        new("secret.slack-token", "Slack token", Severity.High, "secret", "CWE-798", "high",
            R(@"\bxox[baprs]-[0-9A-Za-z-]{10,}"), UseSecretStore),
        new("secret.google-api-key", "Google API key", Severity.High, "secret", "CWE-798", "high",
            R(@"\bAIza[0-9A-Za-z_\-]{35}\b"), UseSecretStore),
        new("secret.stripe-live-key", "Stripe live secret key", Severity.Critical, "secret", "CWE-798", "high",
            R(@"\b[sr]k_live_[0-9A-Za-z]{20,}\b"), UseSecretStore),
        new("secret.jwt", "JSON Web Token in source", Severity.Medium, "secret", "CWE-798", "medium",
            R(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}"), UseSecretStore),
        new("secret.connection-string", "Credentials embedded in a connection URL", Severity.High, "secret", "CWE-798", "medium",
            // Skips obvious placeholders such as user:password@ or user:<secret>@.
            R(@"(?i)\b(?:postgres(?:ql)?|mysql|mariadb|mongodb(?:\+srv)?|amqps?|rediss?|mssql)://[^\s:/@'""<>{}$]+:(?!(?:password|passwd|pass|pwd|secret|x{3,}|\*+|\.{3})@)[^\s@/'""<>{}$]{3,}@[^\s/'""]"),
            UseSecretStore),
        new("secret.generic-assignment", "Hardcoded credential assignment", Severity.Medium, "secret", "CWE-798", "medium",
            R(@"(?i)\b(?:api[_-]?key|secret|token|passwd|password)\b\s*[:=]\s*[""'][A-Za-z0-9_\-]{12,}[""']"), UseSecretStore),

        // --- Cryptography (CWE-327/328) ---------------------------------------
        // Matches uses of the algorithm, not every mention of its name.
        new("crypto.weak-hash", "Weak hash algorithm (MD5/SHA-1)", Severity.Medium, "crypto", "CWE-328", "medium",
            R(@"(?i)(?:\bhashlib\.(?:md5|sha1)\b|(?<![:\w.])(?:md5|sha1)\((?!\))|MessageDigest\.getInstance\(\s*""(?:MD5|SHA-?1)""|\b(?:MD5|SHA1)\.(?:Create|HashData)\b|createHash\(\s*[""'](?:md5|sha1)[""']|\bcrypto:hash\(\s*(?:md5|sha)\s*,|:crypto\.hash\(\s*:(?:md5|sha)\s*,|\berlang:md5\()"),
            "Use SHA-256 or stronger; for passwords use a password-hashing function such as Argon2id or bcrypt."),
        new("crypto.weak-cipher", "Weak cipher or cipher mode (DES/RC4/ECB)", Severity.High, "crypto", "CWE-327", "medium",
            R(@"(?i)(?:AES/ECB|\bMODE_ECB\b|Cipher\.getInstance\(\s*""(?:DES|DESede|RC4)\b|createCipher(?:iv)?\(\s*[""'](?:des|rc4)|\b(?:des_ede3|des_cbc|rc4)\b\s*,|\bDES\.Create\(|\bTripleDES\.Create\()"),
            "Use an authenticated mode of a modern cipher, such as AES-256-GCM or ChaCha20-Poly1305."),

        // --- Insecure configuration -------------------------------------------
        new("tls.verification-disabled", "TLS/certificate verification disabled", Severity.High, "config", "CWE-295", "medium",
            R(@"(?i)(?:verify\s*=\s*False|rejectUnauthorized\s*:\s*false|InsecureSkipVerify\s*[:=]\s*true|CURLOPT_SSL_VERIFYPEER\s*,\s*(?:0|false)|NODE_TLS_REJECT_UNAUTHORIZED[""']?\s*[=:]\s*[""']?0|DangerousAcceptAnyServerCertificateValidator|\{\s*verify\s*,\s*verify_none\s*\}|\bverify:\s*:verify_none\b)"),
            "Keep certificate verification on; trust a private CA explicitly instead of disabling the check."),
        new("config.debug-enabled", "Debug mode enabled", Severity.Low, "config", "CWE-489", "low",
            R(@"(?:\bDEBUG\s*=\s*True\b|\.run\([^)]*\bdebug\s*=\s*True)"),
            "Turn debug mode off outside local development; drive it from configuration."),
        new("config.cors-wildcard", "CORS allows any origin", Severity.Low, "config", "CWE-942", "low",
            R(@"(?i)Access-Control-Allow-Origin[""']?\s*[:,]\s*[""']\*"),
            "Allow only the origins that need access, especially if credentials are sent."),
        new("config.world-writable", "World-writable permissions", Severity.Medium, "config", "CWE-732", "medium",
            R(@"(?:\bchmod\s+(?:-[A-Za-z]+\s+)*0?777\b|\bchmod\([^)]*\b0o?777\b)"),
            "Grant the narrowest permissions that work, e.g. 750 for directories and 640 for files."),

        // --- Injection sinks ---------------------------------------------------
        // In Erlang/Elixir a bare eval( is just a local function name; those have their own rule.
        new("injection.eval", "Dynamic code evaluation", Severity.Medium, "injection", "CWE-95", "low",
            R(@"(?:(?<![A-Za-z0-9_.:])(?<!\bdef\s)(?<!\bfunction\s)eval\s*\(|\bnew Function\s*\()"),
            "Avoid evaluating strings as code; if input can reach this call, parse it into data instead.",
            ExcludedExtensions: [".erl", ".hrl", ".ex", ".exs"]),
        new("injection.eval-beam", "Dynamic code evaluation (Erlang/Elixir)", Severity.Medium, "injection", "CWE-95", "medium",
            R(@"(?:\bCode\.eval_(?:string|quoted|file)\(|\berl_eval:exprs?\(|\bfile:(?:eval|script)\()"),
            "Avoid evaluating strings as code; if input can reach this call, parse it into data instead."),
        new("injection.shell-true", "Shell execution with shell=True", Severity.High, "injection", "CWE-78", "medium",
            R(@"(?i)subprocess\.(?:call|run|Popen|check_output|check_call)\([^)]*shell\s*=\s*True"),
            "Pass the command as an argument list without a shell."),
        new("injection.os-command", "Command passed to a system shell", Severity.Medium, "injection", "CWE-78", "low",
            R(@"(?:\bos\.(?:system|popen)\(|(?<![A-Za-z0-9_])os:cmd\(|:os\.cmd\(|\bSystem\.shell\(|\bchild_process\.exec(?:Sync)?\(|\bRuntime\.getRuntime\(\)\.exec\()"),
            "Run the program directly with an argument list; never build a shell command from input."),
        // The SELECT form needs a WHERE, "=" or "*" so that prose ("select the item from the list") is not SQL.
        new("injection.sql-concatenation", "SQL built by string concatenation or formatting", Severity.High, "injection", "CWE-89", "low",
            R(@"(?i)(?:[""'](?:SELECT\s(?=[^""']*(?:\bWHERE\b|=|\*)).+\sFROM|INSERT\s+INTO|UPDATE\s.+\sSET|DELETE\s+FROM)\b[^""']*[""']\s*(?:\+|%\s*[\(\w]|\.format\()|\bf""(?:SELECT\s.+\sFROM|INSERT\s+INTO|UPDATE\s.+\sSET|DELETE\s+FROM)\b[^""]*\{|\bf'(?:SELECT\s.+\sFROM|INSERT\s+INTO|UPDATE\s.+\sSET|DELETE\s+FROM)\b[^']*\{)"),
            "Use parameterized queries; never splice values into SQL text."),

        // --- Unsafe deserialization (CWE-502) ---------------------------------
        new("deserialization.unsafe", "Deserialization that can execute or instantiate arbitrary data", Severity.High, "deserialization", "CWE-502", "medium",
            R(@"(?:\bc?[pP]ickle\.loads?\(|\byaml\.load\((?![^)]*Loader\s*=\s*(?:yaml\.)?(?:Safe|CSafe)Loader)|\bMarshal\.load\(|\bnew ObjectInputStream\(|\bBinaryFormatter\b)"),
            "Use a data-only format (JSON) or a safe loader; never deserialize untrusted bytes with these APIs."),
        new("deserialization.erlang-term", "binary_to_term without the safe option", Severity.Medium, "deserialization", "CWE-502", "low",
            R(@"\bbinary_to_term\((?![^)]*\bsafe\b)"),
            "Pass [safe] when the binary can come from outside the node, and validate the resulting term."),
    ];
}
