using System.Text.Json;
using System.Text.Json.Nodes;
using Cerberus.Api.Agents;
using Cerberus.Scanning;
using Cerberus.Triage;

namespace Cerberus.Api.Reports;

/// <summary>
/// Writes findings as SARIF 2.1.0, the interchange format code-scanning tools
/// and dashboards read. One SARIF run per source: the scanner's findings and a
/// model's findings are never mixed, so a reader can tell rules from opinions.
/// Snippets are masked where they look like credentials — an export is a file
/// that travels.
/// </summary>
public static class Sarif
{
    public const string ContentType = "application/sarif+json";
    public const string Version = "2.1.0";
    private const string Schema = "https://json.schemastore.org/sarif-2.1.0.json";
    private const string InformationUri = "https://github.com/CommonCapital/Cerberus";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>A SARIF log holding the given runs.</summary>
    public static string Log(params JsonObject?[] runs) =>
        new JsonObject
        {
            ["$schema"] = Schema,
            ["version"] = Version,
            ["runs"] = new JsonArray(runs.Where(r => r is not null).Select(r => (JsonNode)r!).ToArray()),
        }.ToJsonString(Indented);

    /// <summary>The scanner's findings of one scan, with the model's opinions on them (if any) as properties.</summary>
    public static JsonObject ScanRun(
        string scanId, string targetPath, DateTime finishedAt, bool dependencyCheckCompleted,
        IReadOnlyList<Finding> findings, IReadOnlyList<TriageResult> opinions)
    {
        var rules = new List<JsonObject>();
        var ruleIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var verdicts = opinions.ToDictionary(o => o.FindingId);
        var results = new JsonArray();

        foreach (var finding in findings)
        {
            var ruleId = RuleIdOf(finding);
            if (!ruleIndex.TryGetValue(ruleId, out var index))
            {
                index = rules.Count;
                ruleIndex[ruleId] = index;
                rules.Add(Rule(ruleId, finding.Title, finding.Severity, finding.Remediation, finding.Reference,
                    Tags(finding.Category, finding.Cwe, finding.Owasp)));
            }

            var properties = new JsonObject
            {
                ["confidence"] = finding.Confidence,
                ["category"] = finding.Category,
            };
            if (finding.Context is not null) properties["context"] = finding.Context;   // "test" or "docs"
            if (finding.Package is not null)
            {
                properties["package"] = finding.Package;
                properties["version"] = finding.Version;
                if (finding.FixedVersion is not null) properties["fixedVersion"] = finding.FixedVersion;
            }
            if (verdicts.TryGetValue(finding.Id, out var opinion))
            {
                // Advisory: a model's view of this finding. It did not change the finding.
                properties["modelReview"] = new JsonObject
                {
                    ["verdict"] = opinion.Verdict.ToString(),
                    ["model"] = opinion.Model,
                    ["reasoning"] = opinion.Reasoning,
                };
            }

            var message = finding.Package is null
                ? finding.Title
                : $"{finding.Title} ({finding.Package} {finding.Version}{(finding.FixedVersion is null ? "" : $"; fixed in {finding.FixedVersion}")})";
            var result = Result(ruleId, index, finding.Severity, message, finding.File, finding.Line, finding.Evidence, finding.Id, properties);
            if (finding.Suppressed)
            {
                // The target asked for this to be ignored. It is still reported, marked as such.
                result["suppressions"] = new JsonArray(new JsonObject
                {
                    ["kind"] = "inSource",
                    ["justification"] = "Marked to be ignored in the target (cerberus:ignore).",
                });
            }
            results.Add(result);
        }

        var run = Run("Cerberus", rules, results, $"cerberus/scan/{scanId}", finishedAt, new JsonObject
        {
            ["targetPath"] = targetPath,
            ["dependencyCheckCompleted"] = dependencyCheckCompleted,
            ["note"] = "Findings are candidates from pattern-based rules and an advisory database, not confirmed vulnerabilities.",
        });
        return run;
    }

    /// <summary>What the SAST agent reported in one run. A model's candidates, labelled as such.</summary>
    public static JsonObject AgentRun(
        string agentRunId, string model, string targetPath, DateTime finishedAt, bool complete, IReadOnlyList<AgentFinding> findings)
    {
        var prefix = Cerberus.Authorization.ScopePolicy.Normalize(Cerberus.Authorization.TargetType.Path, targetPath) ?? "";
        var rules = new List<JsonObject>();
        var ruleIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var results = new JsonArray();

        foreach (var finding in findings)
        {
            // A model has no rule set; its findings are grouped by the category it chose.
            var ruleId = $"model.{finding.Category}";
            if (!ruleIndex.TryGetValue(ruleId, out var index))
            {
                index = rules.Count;
                ruleIndex[ruleId] = index;
                rules.Add(Rule(ruleId, $"Reported by a model: {finding.Category}", Severity.Medium, null, null, Tags(finding.Category, null, null)));
            }

            var properties = new JsonObject
            {
                ["confidence"] = finding.Confidence,   // "model"
                ["category"] = finding.Category,
                ["source"] = "model",
            };
            if (finding.Cwe is not null) properties["cwe"] = finding.Cwe;
            if (finding.Remediation is not null) properties["remediation"] = finding.Remediation;

            // Paths as in the scanner's run: from the scan root.
            var file = prefix.Length == 0 ? finding.File : $"{prefix}/{finding.File}";
            results.Add(Result(ruleId, index, finding.Severity, $"{finding.Title}: {finding.Description}", file, finding.Line, finding.Evidence, finding.Id, properties));
        }

        return Run($"Cerberus SAST agent ({model})", rules, results, $"cerberus/agent-run/{agentRunId}", finishedAt, new JsonObject
        {
            ["targetPath"] = targetPath,
            ["model"] = model,
            ["complete"] = complete,
            ["note"] = "Reported by a language model that read the code. Candidates for a person to check, not confirmed vulnerabilities.",
        });
    }

    private static JsonObject Run(string tool, List<JsonObject> rules, JsonArray results, string automationId, DateTime finishedAt, JsonObject properties) =>
        new()
        {
            ["tool"] = new JsonObject
            {
                ["driver"] = new JsonObject
                {
                    ["name"] = tool,
                    ["informationUri"] = InformationUri,
                    ["rules"] = new JsonArray(rules.Select(r => (JsonNode)r).ToArray()),
                },
            },
            ["automationDetails"] = new JsonObject { ["id"] = automationId },
            ["invocations"] = new JsonArray(new JsonObject
            {
                ["executionSuccessful"] = true,
                ["endTimeUtc"] = finishedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            }),
            ["results"] = results,
            ["properties"] = properties,
        };

    private static JsonObject Rule(string id, string title, Severity severity, string? remediation, string? reference, JsonArray tags)
    {
        var rule = new JsonObject
        {
            ["id"] = id,
            ["name"] = id,
            ["shortDescription"] = new JsonObject { ["text"] = title },
            ["defaultConfiguration"] = new JsonObject { ["level"] = Level(severity) },
            ["properties"] = new JsonObject
            {
                ["tags"] = tags,
                // The number code-scanning dashboards sort by. A bucket per severity, not a CVSS score.
                ["security-severity"] = SecuritySeverity(severity),
            },
        };
        if (remediation is not null) rule["help"] = new JsonObject { ["text"] = remediation };
        if (reference is not null && Uri.TryCreate(reference, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            rule["helpUri"] = reference;
        }
        return rule;
    }

    private static JsonObject Result(
        string ruleId, int ruleIndex, Severity severity, string message, string file, int line, string evidence, string findingId, JsonObject properties) =>
        new()
        {
            ["ruleId"] = ruleId,
            ["ruleIndex"] = ruleIndex,
            ["level"] = Level(severity),
            ["message"] = new JsonObject { ["text"] = message },
            ["locations"] = new JsonArray(new JsonObject
            {
                ["physicalLocation"] = new JsonObject
                {
                    ["artifactLocation"] = new JsonObject { ["uri"] = UriOf(file), ["uriBaseId"] = "%SRCROOT%" },
                    ["region"] = new JsonObject
                    {
                        ["startLine"] = Math.Max(1, line),
                        ["snippet"] = new JsonObject { ["text"] = TriagePrompt.Redact(evidence) },
                    },
                },
            }),
            // Stable across runs for the same finding, so a reader can track it.
            ["partialFingerprints"] = new JsonObject { ["cerberusFindingId/v1"] = findingId },
            ["properties"] = properties,
        };

    /// <summary>The rule a scanner finding came from: its rule id, or for a dependency the advisory.</summary>
    private static string RuleIdOf(Finding finding)
    {
        if (finding.VulnId is { Length: > 0 } vuln) return vuln;
        var colon = finding.Id.IndexOf(':');
        return colon > 0 ? finding.Id[..colon] : finding.Category;
    }

    private static JsonArray Tags(string category, string? cwe, string? owasp)
    {
        var tags = new JsonArray { "security", category };
        if (cwe is not null) tags.Add($"external/cwe/{cwe.ToLowerInvariant()}");
        if (owasp is not null) tags.Add($"owasp-top10-2021/{owasp}");
        return tags;
    }

    private static string Level(Severity severity) => severity switch
    {
        Severity.Critical or Severity.High => "error",
        Severity.Medium => "warning",
        _ => "note",
    };

    private static string SecuritySeverity(Severity severity) => severity switch
    {
        Severity.Critical => "9.5",
        Severity.High => "8.0",
        Severity.Medium => "5.5",
        Severity.Low => "3.0",
        _ => "0.0",
    };

    /// <summary>A relative path as a URI reference: forward slashes, each segment escaped.</summary>
    private static string UriOf(string file) =>
        string.Join('/', file.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));
}
