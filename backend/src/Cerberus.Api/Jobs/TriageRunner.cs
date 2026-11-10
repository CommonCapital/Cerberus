using System.Text.Json;
using System.Text.Json.Serialization;
using Cerberus.Api.Audit;
using Cerberus.Api.Settings;
using Cerberus.Authorization;
using Cerberus.Core.Models;
using Cerberus.Data;
using Cerberus.Scanning;
using Cerberus.Triage;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api.Jobs;

public sealed record TriageJobParameters(string ScanId, string Model, int MaxFindings = 15, bool IncludeTestsAndDocs = false);

/// <summary>
/// Runs LLM triage over a stored scan's findings (PLAN.md §9, "LLM layer") and
/// saves each opinion on the scan as it arrives, so a cancelled or timed-out
/// run keeps what it finished. Authorization is the caller's job.
/// </summary>
public sealed class TriageRunner(
    FindingTriager triager, ApiKeyService keys, CerberusDbContext db, AuditLog audit, ScanOptions scanOptions)
{
    public const int MaxFindingsLimit = 100;

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<int> RunAsync(string userId, TriageJobParameters parameters, CancellationToken ct)
    {
        var scan = await db.Scans.FirstOrDefaultAsync(s => s.Id == parameters.ScanId && s.UserId == userId, ct)
            ?? throw new InvalidOperationException($"Scan {parameters.ScanId} no longer exists.");

        var selected = SelectFor(scan.Findings, parameters);

        var provider = ModelRegistry.Find(parameters.Model)?.Provider
            ?? throw new InvalidOperationException($"Unsupported model: {parameters.Model}");
        var credentials = await keys.ResolveCredentialsAsync(userId, ct);
        var results = ReadTriage(scan.Triage).ToDictionary(r => r.FindingId);
        var files = new Dictionary<string, string[]?>(StringComparer.Ordinal);
        var done = 0;

        try
        {
            foreach (var finding in selected)
            {
                ct.ThrowIfCancellationRequested();
                if (!files.TryGetValue(finding.File, out var lines))
                {
                    files[finding.File] = lines = ReadConfined(finding.File);
                }

                var result = await triager.TriageAsync(finding, lines, parameters.Model, credentials, DateTime.UtcNow, ct);
                results[result.FindingId] = result;   // a newer opinion replaces an older one
                done++;

                scan.Triage = JsonSerializer.Serialize(results.Values, JsonOptions);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        finally
        {
            // Recorded even when stopped part-way: code may already have left this machine.
            var where = FindingTriager.IsLocal(provider) ? "a local model" : $"{provider} (secrets redacted)";
            await audit.AppendAsync(userId, "triage.run", done == selected.Count ? AuditOutcome.Ok : AuditOutcome.Error,
                $"Triage of scan {scan.Id}: {done} of {selected.Count} finding(s) reviewed by {parameters.Model}; code context was sent to {where}.",
                scan.EngagementId, scan.TargetPath, CapabilityTier.T0, CancellationToken.None);
        }
        return done;
    }

    /// <summary>The findings a triage run with these parameters would review, in order.</summary>
    public static IReadOnlyList<Finding> SelectFor(string findingsJson, TriageJobParameters parameters)
    {
        List<Finding> findings;
        try
        {
            findings = JsonSerializer.Deserialize<List<Finding>>(findingsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            findings = [];
        }
        return FindingTriager.Select(
            findings, Math.Clamp(parameters.MaxFindings, 1, MaxFindingsLimit), parameters.IncludeTestsAndDocs);
    }

    public static IReadOnlyList<TriageResult> ReadTriage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<TriageResult>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The file's lines, or null if it is gone, unreadable, a link, or outside the scan root.</summary>
    private string[]? ReadConfined(string relativeFile)
    {
        try
        {
            var root = Path.GetFullPath(scanOptions.ScanRoot);
            var full = Path.GetFullPath(Path.Combine(root, relativeFile));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;

            var info = new FileInfo(full);
            if (!info.Exists || info.LinkTarget is not null || info.Length > 1_000_000) return null;
            return File.ReadAllText(full).Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
