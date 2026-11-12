using System.Text;
using System.Text.Json;
using Cerberus.Api.Audit;
using Cerberus.Api.Auth;
using Cerberus.Api.Jobs;
using Cerberus.Authorization;
using Cerberus.Api.Chat;
using Cerberus.Api.Settings;
using Cerberus.Compliance;
using Cerberus.Core.Models;
using Cerberus.Data;
using Cerberus.Scanning;
using Microsoft.EntityFrameworkCore;

namespace Cerberus.Api;

public static class Endpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static void MapCerberusEndpoints(this WebApplication app)
    {
        Cerberus.Api.Health.HealthEndpoints.MapHealthEndpoints(app);

        var auth = app.MapGroup("/auth");
        auth.MapPost("/register", Register);
        auth.MapPost("/login", Login);
        auth.MapPost("/logout", Logout);

        var sessions = app.MapGroup("/sessions").AddEndpointFilter<RequireAuthFilter>();
        sessions.MapGet("/", ListSessions);
        sessions.MapGet("/{id}", GetSession);
        sessions.MapPost("/", CreateSession);
        sessions.MapPost("/{id}/compact", CompactSession);
        sessions.MapPost("/{id}/title", WriteSessionTitle);
        sessions.MapPut("/{id}/title", SetSessionTitle);
        sessions.MapPost("/{id}/fork", ForkSession);
        sessions.MapGet("/{id}/export", ExportSession);

        var settings = app.MapGroup("/settings/keys").AddEndpointFilter<RequireAuthFilter>();
        settings.MapGet("/", ListApiKeys);
        settings.MapPut("/", SetApiKey);
        settings.MapDelete("/{provider}", DeleteApiKey);

        app.MapPost("/chat", StreamChat).AddEndpointFilter<RequireAuthFilter>();

        var scans = app.MapGroup("/scans").AddEndpointFilter<RequireAuthFilter>();
        scans.MapGet("/", ListScans);
        scans.MapGet("/{id}", GetScan);

        app.MapPost("/scan", RunScan).AddEndpointFilter<RequireAuthFilter>();

        // --- Phase 2: Compliance (ESAP/ISO/OWASP catalog engine) ------------
        var catalogs = app.MapGroup("/compliance/catalogs").AddEndpointFilter<RequireAuthFilter>();
        catalogs.MapGet("/", ListCatalogs);
        catalogs.MapGet("/{id}", GetCatalog);

        var compliance = app.MapGroup("/compliance").AddEndpointFilter<RequireAuthFilter>();
        compliance.MapGet("/", ListAssessments);
        compliance.MapGet("/{id}", GetAssessment);
        compliance.MapGet("/{id}/export", ExportAssessment);
        compliance.MapPost("/{id}/import", ImportAssessment);
        compliance.MapPut("/{id}/responses/{controlId}", SetControlResponse);
        compliance.MapDelete("/{id}/responses/{controlId}", DeleteControlResponse);
        compliance.MapPost("/{id}/suggestions/{controlId}/accept", AcceptSuggestion);
        compliance.MapDelete("/{id}/suggestions/{controlId}", DismissSuggestion);
        compliance.MapPost("/assess", RunAssessment);
    }

    // --- compliance (Phase 2: catalog engine + matrix) ----------------------

    private static IResult ListCatalogs(CatalogLoader loader) =>
        Results.Json(loader.All.Select(CatalogLoader.Summarize), JsonOptions);

    private static IResult GetCatalog(string id, CatalogLoader loader)
    {
        var catalog = loader.Get(id);
        return catalog is null
            ? Results.Json(new { error = "Catalog not found" }, statusCode: 404)
            : Results.Json(catalog, JsonOptions);
    }

    private static async Task<IResult> RunAssessment(
        ComplianceRequest body, HttpContext http, CerberusDbContext db,
        CatalogLoader loader, AssessmentRunner runner, AuditLog audit, RunPolicy policy, CancellationToken ct)
    {
        var userId = UserId(http);
        var catalogId = string.IsNullOrWhiteSpace(body.CatalogId) ? "enterprise-sap" : body.CatalogId!;

        var catalog = loader.Get(catalogId);
        if (catalog is null)
        {
            return Results.Json(new { error = $"Unknown catalog '{catalogId}'" }, statusCode: 400);
        }

        // Findings come from a persisted scan (preferred) or a fresh scan of a path.
        Scan? scan = null;
        string targetPath;
        string? engagementId = string.IsNullOrWhiteSpace(body.EngagementId) ? null : body.EngagementId;

        if (!string.IsNullOrWhiteSpace(body.ScanId))
        {
            scan = await db.Scans.FirstOrDefaultAsync(s => s.Id == body.ScanId && s.UserId == userId, ct);
            if (scan is null) return Results.Json(new { error = "Scan not found" }, statusCode: 404);

            // An assessment of a scan belongs to that scan's engagement.
            if (engagementId is not null && engagementId != scan.EngagementId)
            {
                return Results.Json(new { error = "That scan was not run under this engagement" }, statusCode: 400);
            }
            engagementId = scan.EngagementId;
            targetPath = scan.TargetPath;
        }
        else
        {
            targetPath = string.IsNullOrWhiteSpace(body.Path) ? "." : body.Path!;
        }

        if (EngagementEndpoints.RequireEngagement(policy, engagementId) is { } required) return required;
        if (engagementId is not null &&
            await EngagementEndpoints.AuthorizeRunAsync(db, audit, userId, engagementId,
                "compliance.assess", CapabilityTier.T0, targetPath, ct) is { } denied)
        {
            return denied;
        }

        AssessmentRun run;
        try
        {
            run = await runner.RunAsync(userId, engagementId, catalog, scan, targetPath, body.Sca ?? true, ct);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 400);
        }
        var (assessment, report, dependencyCheckCompleted) = run;
        var scanId = scan?.Id;

        return Results.Json(new
        {
            id = assessment.Id,
            catalogId = report.CatalogId,
            catalogName = report.CatalogName,
            catalogVersion = report.CatalogVersion,
            targetPath,
            scanId,
            engagementId,
            dependencyCheckCompleted,
            createdAt = assessment.CreatedAt,
            summary = new
            {
                report.TotalControls,
                report.Assessed,
                report.Compliant,
                report.NonCompliant,
                report.NotAssessed,
                report.Score,
            },
            controls = report.Controls,
        }, JsonOptions);
    }

    private static async Task<IResult> ListAssessments(HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var items = await db.ComplianceAssessments
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new
            {
                a.Id, a.CatalogId, a.TargetPath, a.ScanId, a.EngagementId,
                a.TotalControls, a.Compliant, a.NonCompliant, a.NotAssessed, a.Score, a.CreatedAt,
            })
            .ToListAsync(ct);
        return Results.Json(items);
    }

    /// <summary>Download an assessment as a vendor-assurance spreadsheet (.xlsx).</summary>
    private static async Task<IResult> ExportAssessment(
        string id, HttpContext http, CerberusDbContext db, CatalogLoader loader, CancellationToken ct)
    {
        var a = await FindAssessment(db, id, UserId(http), ct);
        if (a is null) return AssessmentNotFound();
        if (ReadMatrix(a) is not { } controls) return MatrixUnreadable();

        var info = new AssessmentInfo(a.Id, a.CatalogId, a.TargetPath, a.ScanId, a.CreatedAt, a.Score);
        var bytes = ComplianceWorkbook.Build(info, loader.Get(a.CatalogId), controls, ReadResponses(a));
        return Results.File(bytes, ComplianceWorkbook.ContentType, ComplianceWorkbook.FileName(info));
    }

    /// <summary>
    /// Import operator answers from a completed export. The request body is the
    /// raw .xlsx file. Valid rows are saved; invalid rows are reported per row.
    /// </summary>
    private static async Task<IResult> ImportAssessment(
        string id, HttpContext http, CerberusDbContext db, CatalogLoader loader, CancellationToken ct)
    {
        var a = await FindAssessment(db, id, UserId(http), ct);
        if (a is null) return AssessmentNotFound();
        if (ReadMatrix(a) is not { } controls) return MatrixUnreadable();

        // Buffer the upload with a hard cap; ClosedXML needs a seekable stream.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxImportBytes)
            {
                return Results.Json(new { error = $"File is larger than {MaxImportBytes / (1024 * 1024)} MB" }, statusCode: 413);
            }
            buffer.Write(chunk, 0, read);
        }
        if (buffer.Length == 0)
        {
            return Results.Json(new { error = "Send the .xlsx file as the request body" }, statusCode: 400);
        }
        buffer.Position = 0;

        var responses = ReadResponses(a);
        WorkbookImport result;
        try
        {
            result = ComplianceWorkbook.ReadResponses(buffer, loader.Get(a.CatalogId), controls, responses, DateTime.UtcNow);
        }
        catch (InvalidDataException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 400);
        }

        if (result.Accepted.Count > 0)
        {
            foreach (var response in result.Accepted) responses[response.ControlId] = response;
            a.Responses = JsonSerializer.Serialize(responses.Values, JsonOptions);
            await db.SaveChangesAsync(ct);
        }

        return Results.Json(new
        {
            imported = result.Accepted.Count,
            unchanged = result.Unchanged,
            errors = result.Errors,
            effective = OperatorResponses.Summarize(controls, responses),
        }, JsonOptions);
    }

    private static async Task<IResult> SetControlResponse(
        string id, string controlId, ControlResponseRequest body, HttpContext http,
        CerberusDbContext db, CatalogLoader loader, CancellationToken ct)
    {
        var a = await FindAssessment(db, id, UserId(http), ct);
        if (a is null) return AssessmentNotFound();
        if (ReadMatrix(a) is not { } controls) return MatrixUnreadable();

        var control = controls.FirstOrDefault(c => string.Equals(c.ControlId, controlId, StringComparison.OrdinalIgnoreCase));
        if (control is null) return Results.Json(new { error = "Control is not part of this assessment" }, statusCode: 404);

        var (response, error) = OperatorResponses.Validate(
            control.ControlId,
            new OperatorResponseInput(body.Verdict, body.Justification, body.ActionPlan, body.TimeLimitDays),
            loader.Get(a.CatalogId), DateTime.UtcNow);
        if (response is null) return Results.Json(new { error }, statusCode: 400);

        var responses = ReadResponses(a);
        responses[control.ControlId] = response;
        a.Responses = JsonSerializer.Serialize(responses.Values, JsonOptions);
        await db.SaveChangesAsync(ct);

        return Results.Json(new { response, effective = OperatorResponses.Summarize(controls, responses) }, JsonOptions);
    }

    private static async Task<IResult> DeleteControlResponse(
        string id, string controlId, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var a = await FindAssessment(db, id, UserId(http), ct);
        if (a is null) return AssessmentNotFound();
        if (ReadMatrix(a) is not { } controls) return MatrixUnreadable();

        var responses = ReadResponses(a);
        if (!responses.Remove(controlId))
        {
            return Results.Json(new { error = "No operator response for that control" }, statusCode: 404);
        }
        a.Responses = JsonSerializer.Serialize(responses.Values, JsonOptions);
        await db.SaveChangesAsync(ct);

        return Results.Json(new { effective = OperatorResponses.Summarize(controls, responses) }, JsonOptions);
    }

    private static async Task<IResult> GetAssessment(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var a = await FindAssessment(db, id, UserId(http), ct);
        if (a is null) return AssessmentNotFound();
        if (ReadMatrix(a) is not { } controls) return MatrixUnreadable();

        var responses = ReadResponses(a);
        var suggestions = Agents.EvidenceAgent.ReadSuggestions(a);
        return Results.Json(new
        {
            a.Id, a.CatalogId, a.TargetPath, a.ScanId, a.CreatedAt,
            // What the scanner established on its own.
            summary = new { a.TotalControls, a.Compliant, a.NonCompliant, a.NotAssessed, a.Score },
            // Operator answers applied on top of the automated verdicts.
            effective = OperatorResponses.Summarize(controls, responses),
            controls = controls.Select(c => new
            {
                c.ControlId, c.Section, c.Title, c.Verdict, c.AutoEvaluable, c.Rationale, c.Evidence,
                response = responses.GetValueOrDefault(c.ControlId),
                // A model's suggested answer, if one was asked for. Advisory until accepted.
                suggestion = suggestions.GetValueOrDefault(c.ControlId),
            }),
        }, JsonOptions);
    }

    /// <summary>
    /// The operator makes a model's suggestion their own answer. The body may add
    /// what the response schema requires beyond the suggestion (an action plan, a time limit).
    /// </summary>
    private static async Task<IResult> AcceptSuggestion(
        string id, string controlId, ControlResponseRequest? body, HttpContext http,
        CerberusDbContext db, CatalogLoader loader, Audit.AuditLog audit, CancellationToken ct)
    {
        var a = await FindAssessment(db, id, UserId(http), ct);
        if (a is null) return AssessmentNotFound();
        if (ReadMatrix(a) is not { } controls) return MatrixUnreadable();

        var suggestions = Agents.EvidenceAgent.ReadSuggestions(a);
        if (!suggestions.TryGetValue(controlId, out var suggestion))
        {
            return Results.Json(new { error = "There is no suggestion for that control" }, statusCode: 404);
        }
        if (suggestion.Suggestion == Agents.EvidenceAgent.CannotTell)
        {
            return Results.Json(new { error = "The model could not tell; there is nothing to accept. Answer the control yourself." }, statusCode: 400);
        }

        var places = suggestion.Evidence.Count == 0 ? "" : " Evidence: " + string.Join("; ", suggestion.Evidence.Select(e => $"{e.File}:{e.Line}")) + ".";
        var justification = string.IsNullOrWhiteSpace(body?.Justification)
            ? $"Accepted from a suggestion by {suggestion.Model}: {suggestion.Reasoning}{places}"
            : body.Justification;
        var (response, error) = OperatorResponses.Validate(
            suggestion.ControlId,
            new OperatorResponseInput(suggestion.Suggestion, justification, body?.ActionPlan, body?.TimeLimitDays),
            loader.Get(a.CatalogId), DateTime.UtcNow);
        if (response is null) return Results.Json(new { error }, statusCode: 400);

        var responses = ReadResponses(a);
        responses[suggestion.ControlId] = response;
        a.Responses = JsonSerializer.Serialize(responses.Values, JsonOptions);
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(UserId(http), "evidence.accept", Audit.AuditOutcome.Ok,
            $"Operator accepted the suggestion of {suggestion.Model} for control {suggestion.ControlId} of assessment {a.Id}: {suggestion.Suggestion}.",
            a.EngagementId, a.TargetPath, ct: ct);

        return Results.Json(new { response, effective = OperatorResponses.Summarize(controls, responses) }, JsonOptions);
    }

    private static async Task<IResult> DismissSuggestion(
        string id, string controlId, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var a = await FindAssessment(db, id, UserId(http), ct);
        if (a is null) return AssessmentNotFound();

        var suggestions = Agents.EvidenceAgent.ReadSuggestions(a);
        if (!suggestions.Remove(controlId))
        {
            return Results.Json(new { error = "There is no suggestion for that control" }, statusCode: 404);
        }
        Agents.EvidenceAgent.WriteSuggestions(a, suggestions.Values);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private const int MaxImportBytes = 5 * 1024 * 1024;

    private static Task<ComplianceAssessment?> FindAssessment(
        CerberusDbContext db, string id, string userId, CancellationToken ct) =>
        db.ComplianceAssessments.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);

    private static IResult AssessmentNotFound() =>
        Results.Json(new { error = "Assessment not found" }, statusCode: 404);

    private static IResult MatrixUnreadable() =>
        Results.Json(new { error = "Stored assessment could not be read" }, statusCode: 500);

    private static List<ControlResult>? ReadMatrix(ComplianceAssessment a)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ControlResult>>(a.Matrix, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Operator answers by control id. Empty for rows written before the column existed.</summary>
    private static Dictionary<string, OperatorResponse> ReadResponses(ComplianceAssessment a)
    {
        var result = new Dictionary<string, OperatorResponse>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(a.Responses)) return result;
        try
        {
            foreach (var r in JsonSerializer.Deserialize<List<OperatorResponse>>(a.Responses, JsonOptions) ?? [])
            {
                result[r.ControlId] = r;
            }
        }
        catch (JsonException)
        {
            // Treat unreadable answers as none rather than failing the whole assessment.
        }
        return result;
    }

    // --- scanning (Phase 1: Scan & Detect, MVP) -----------------------------

    private static async Task<IResult> RunScan(
        ScanRequest body, HttpContext http, CerberusDbContext db,
        ScanRunner runner, AuditLog audit, RunPolicy policy, CancellationToken ct)
    {
        var userId = UserId(http);
        var targetPath = string.IsNullOrWhiteSpace(body.Path) ? "." : body.Path!;
        var engagementId = string.IsNullOrWhiteSpace(body.EngagementId) ? null : body.EngagementId;

        // Scope gate: a run under an engagement must be inside its scope.
        if (EngagementEndpoints.RequireEngagement(policy, engagementId) is { } required) return required;
        if (engagementId is not null &&
            await EngagementEndpoints.AuthorizeRunAsync(db, audit, userId, engagementId,
                "scan.run", CapabilityTier.T0, targetPath, ct) is { } denied)
        {
            return denied;
        }

        // A synchronous request has nobody to ask, so it takes the choice up front.
        var exclusions = string.IsNullOrWhiteSpace(body.Exclusions) ? ScanExclusions.Honour : body.Exclusions.Trim().ToLowerInvariant();
        if (!ScanExclusions.IsValid(exclusions, allowAsk: false))
        {
            return Results.Json(new { error = "exclusions must be honour or ignore (use a scan job to be asked)" }, statusCode: 400);
        }

        ScanRun run;
        try
        {
            run = await runner.RunAsync(userId, engagementId, targetPath, body.Sca ?? true, ct, exclusions: exclusions);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 400);
        }
        var (scan, result, scaResult, findings) = run;

        return Results.Json(new
        {
            id = scan.Id,
            engagementId,
            targetPath = scan.TargetPath,
            createdAt = scan.CreatedAt,
            filesScanned = result.FilesScanned,
            filesSkipped = result.FilesSkipped,
            filesIgnored = result.FilesIgnored,
            manifestsScanned = scaResult.ManifestsScanned,
            dependenciesScanned = scaResult.DependenciesScanned,
            scaCompleted = scaResult.Completed,
            findingCount = scan.FindingCount,
            suppressedCount = result.SuppressedCount,
            truncated = result.Truncated,
            findings,
        }, JsonOptions);
    }

    private static async Task<IResult> ListScans(HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var scans = await db.Scans
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.Id, s.EngagementId, s.TargetPath, s.FilesScanned, s.FindingCount, s.ScaCompleted, s.CreatedAt })
            .ToListAsync(ct);
        return Results.Json(scans);
    }

    private static async Task<IResult> GetScan(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var scan = await db.Scans.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (scan is null) return Results.Json(new { error = "Scan not found" }, statusCode: 404);

        // Each finding with its fingerprint and, where the operator has decided on it, that decision.
        var decisions = await FindingDecisions.ForUserAsync(db, userId, ct);
        var fingerprints = FindingDecisions.ReadFindings(scan.Findings)
            .GroupBy(f => f.Id).ToDictionary(g => g.Key, g => FindingDecisions.Fingerprint(g.First()));
        var findings = MaskEvidence(ParseStoredMessages(scan.Findings));
        foreach (var finding in findings.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            var findingId = (finding["id"] ?? finding["Id"])?.GetValue<string>();
            if (findingId is null || !fingerprints.TryGetValue(findingId, out var fingerprint)) continue;
            finding["fingerprint"] = fingerprint;
            finding["decision"] = decisions.TryGetValue(fingerprint, out var decision)
                ? JsonSerializer.SerializeToNode(FindingDecisions.Describe(decision))
                : null;
        }

        return Results.Json(new
        {
            scan.Id,
            scan.TargetPath,
            scan.FilesScanned,
            scan.FindingCount,
            scan.ScaCompleted,
            scan.CreatedAt,
            // Evidence is masked when a scan is stored; scans stored before that are masked on the way out.
            findings,
            // Advisory LLM opinions, keyed to findings by findingId. They never alter findings or compliance.
            triage = ParseStoredMessages(scan.Triage),
        });
    }

    private static List<System.Text.Json.Nodes.JsonNode?> MaskEvidence(List<JsonElement> findings) =>
        findings.Select(element =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText());
            if (node is System.Text.Json.Nodes.JsonObject finding)
            {
                var key = finding.ContainsKey("evidence") ? "evidence" : finding.ContainsKey("Evidence") ? "Evidence" : null;
                if (key is not null && finding[key] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text))
                {
                    finding[key] = Cerberus.Scanning.SecretMasker.Mask(text);
                }
            }
            return node;
        }).ToList();

    // --- settings: per-user model API keys ----------------------------------

    private static async Task<IResult> ListApiKeys(HttpContext http, ApiKeyService keys, CancellationToken ct)
    {
        var providers = await keys.ListProvidersAsync(UserId(http), ct);
        // Never return the key material — only which providers are configured.
        var result = ApiKeyService.SupportedProviders
            .Select(p => new { provider = p, hasKey = providers.Contains(p) });
        return Results.Json(result);
    }

    private static async Task<IResult> SetApiKey(SetApiKeyRequest body, HttpContext http, ApiKeyService keys, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Provider) || !ApiKeyService.IsSupportedProvider(body.Provider))
        {
            return Results.Json(new { error = $"Unsupported provider. Use one of: {string.Join(", ", ApiKeyService.SupportedProviders)}" }, statusCode: 400);
        }
        if (string.IsNullOrWhiteSpace(body.ApiKey))
        {
            return Results.Json(new { error = "apiKey is required" }, statusCode: 400);
        }

        await keys.SetAsync(UserId(http), body.Provider, body.ApiKey, ct);
        return Results.Json(new { provider = body.Provider, hasKey = true });
    }

    private static async Task<IResult> DeleteApiKey(string provider, HttpContext http, ApiKeyService keys, CancellationToken ct)
    {
        var removed = await keys.DeleteAsync(UserId(http), provider, ct);
        return removed
            ? Results.Json(new { provider, hasKey = false })
            : Results.Json(new { error = "No key set for that provider" }, statusCode: 404);
    }

    // --- auth ---------------------------------------------------------------

    private static async Task<IResult> Register(CredentialsRequest body, AuthService auth, CancellationToken ct)
    {
        if (!IsValid(body))
        {
            return Results.Json(new { error = "A valid email and a password (min 6 characters) are required" }, statusCode: 400);
        }
        try
        {
            var token = await auth.RegisterAsync(body.Email, body.Password, ct);
            return Results.Json(new { token }, statusCode: 201);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 409);
        }
    }

    private static async Task<IResult> Login(
        CredentialsRequest body, HttpContext http, AuthService auth, LoginThrottle throttle, CerberusDbContext db,
        Audit.AuditLog audit, CancellationToken ct)
    {
        if (!IsValid(body))
        {
            return Results.Json(new { error = "A valid email and a password (min 6 characters) are required" }, statusCode: 400);
        }

        // Too many wrong passwords in a row: refused for a while, whatever the password.
        if (throttle.LockedFor(body.Email) is { } wait)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
            return Results.Json(new { error = $"Too many failed sign-ins. Try again in {Math.Ceiling(wait.TotalMinutes)} minute(s)." }, statusCode: 429);
        }

        var token = await auth.LoginAsync(body.Email, body.Password, ct);
        if (token is not null)
        {
            throttle.Succeeded(body.Email);
            return Results.Json(new { token });
        }

        if (throttle.Failed(body.Email))
        {
            // For an account that exists, its owner should be able to see that someone was guessing.
            var userId = await db.Users.Where(u => u.Email == body.Email).Select(u => u.Id).FirstOrDefaultAsync(ct);
            if (userId is not null)
            {
                await audit.AppendAsync(userId, "auth.lockout", Audit.AuditOutcome.Denied,
                    $"Sign-in locked for {LoginThrottle.Lock.TotalMinutes} minutes after {LoginThrottle.MaxFailures} failed attempts in a row.", ct: ct);
            }
        }
        return Results.Json(new { error = "Invalid email or password" }, statusCode: 401);
    }

    private static async Task<IResult> Logout(HttpContext http, AuthService auth, CancellationToken ct)
    {
        var token = RequireAuthFilter.ExtractBearerToken(http.Request.Headers.Authorization);
        if (token is not null) await auth.LogoutAsync(token, ct);
        return Results.Json(new { success = true });
    }

    private static bool IsValid(CredentialsRequest body) =>
        IsEmail(body.Email) && body.Password?.Length >= 6;

    /// <summary>A plain address of the form local@domain.tld — what the TypeScript server's schema accepted.</summary>
    private static bool IsEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254 || value.Any(char.IsWhiteSpace)) return false;
        if (!System.Net.Mail.MailAddress.TryCreate(value, out var address) || address.Address != value) return false;
        var host = address.Host;
        return host.Contains('.') && !host.StartsWith('.') && !host.EndsWith('.') && !host.Contains("..");
    }

    // --- sessions -----------------------------------------------------------

    private static async Task<IResult> ListSessions(HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var sessions = await db.Sessions
            // A helper's conversation belongs to the chat that started it; it is not one of the operator's own.
            .Where(s => s.UserId == userId && s.ParentId == null)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.Id, s.Title, s.CreatedAt })
            .ToListAsync(ct);
        return Results.Json(sessions);
    }

    private static async Task<IResult> GetSession(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (session is null) return Results.Json(new { error = "Session not found" }, statusCode: 404);

        return Results.Json(new
        {
            session.Id,
            session.UserId,
            session.Title,
            session.CreatedAt,
            session.UpdatedAt,
            messages = ParseStoredMessages(session.Messages),
            // Set once the chat was compacted: the model gets a summary in place of the messages up to this one.
            summarizedThrough = session.SummarizedThrough,
            // The agent's task list as it last set it (see updateTasks).
            tasks = Cerberus.Api.Chat.TaskList.Read(session.Tasks),
            parentId = session.ParentId,
        });
    }

    /// <summary>
    /// Summarises the earlier part of a session now, whatever its size — the operator's "/compact".
    /// The messages stay; the model gets the summary in their place from the next reply on.
    /// </summary>
    private static async Task<IResult> CompactSession(
        string id, CompactRequest? body, HttpContext http, CerberusDbContext db, ApiKeyService keys, ChatCompactor compactor, CancellationToken ct)
    {
        var userId = UserId(http);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (session is null) return Results.Json(new { error = "Session not found" }, statusCode: 404);

        var messages = ParseStoredMessages(session.Messages)
            .Select(m => System.Text.Json.Nodes.JsonNode.Parse(m.GetRawText()) as System.Text.Json.Nodes.JsonObject)
            .Where(m => m is not null).Select(m => m!).ToList();
        // The model that wrote the last reply, unless another is named.
        var model = body?.Model ?? messages.Select(m => m["metadata"]?["model"]?.GetValue<string>()).LastOrDefault(m => m is not null);
        if (model is null || !ModelRegistry.IsSupported(model))
        {
            return Results.Json(new { error = "model is required and must be a listed model or provider:model (anthropic:, openai:, deepseek: or ollama:)" }, statusCode: 400);
        }

        Compaction? compaction;
        try
        {
            compaction = await compactor.CompactAsync(messages, session.Summary, session.SummarizedThrough, model, await keys.ResolveCredentialsAsync(userId, ct), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Json(new { error = $"The summary could not be written: {ex.Message}" }, statusCode: 502);
        }
        if (compaction is null)
        {
            return Results.Json(new { error = "There is nothing to compact yet: the last exchange is always kept as it is." }, statusCode: 409);
        }

        session.Summary = compaction.Summary;
        session.SummarizedThrough = compaction.Through;
        await db.SaveChangesAsync(ct);
        return Results.Json(new { messages = compaction.Messages, summary = compaction.Summary, model });
    }

    /// <summary>
    /// Has the model name the session from its first prompt. Asked for by the client once the
    /// first reply is in; a title that cannot be written leaves the session named as it was.
    /// </summary>
    private static async Task<IResult> WriteSessionTitle(
        string id, CompactRequest? body, HttpContext http, CerberusDbContext db, ApiKeyService keys, SessionTitler titler, CancellationToken ct)
    {
        var userId = UserId(http);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (session is null) return Results.Json(new { error = "Session not found" }, statusCode: 404);

        var messages = ParseStoredMessages(session.Messages)
            .Select(m => System.Text.Json.Nodes.JsonNode.Parse(m.GetRawText()) as System.Text.Json.Nodes.JsonObject)
            .Where(m => m is not null).Select(m => m!).ToList();
        // The model that wrote the last reply, unless another is named: no new provider sees the message.
        var model = body?.Model ?? messages.Select(m => m["metadata"]?["model"]?.GetValue<string>()).LastOrDefault(m => m is not null);
        if (model is null || !ModelRegistry.IsSupported(model))
        {
            return Results.Json(new { error = "model is required and must be a listed model or provider:model (anthropic:, openai:, deepseek: or ollama:)" }, statusCode: 400);
        }
        if (SessionTitler.FirstPrompt(messages) is not { } prompt)
        {
            return Results.Json(new { error = "There is nothing to name the session after yet." }, statusCode: 409);
        }

        string? title;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(60));   // a title is not worth waiting long for
            title = await titler.WriteAsync(prompt, model, await keys.ResolveCredentialsAsync(userId, ct), limit.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Results.Json(new { error = $"The title could not be written: {(ex is OperationCanceledException ? "the model took too long." : ex.Message)}" }, statusCode: 502);
        }
        if (title is null) return Results.Json(new { error = "The model returned nothing usable as a title." }, statusCode: 502);

        // Only the title: a reply being stored at the same moment must not be overwritten with what was read above.
        await db.Sessions.Where(s => s.Id == id && s.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, title), ct);
        return Results.Json(new { title, model });
    }

    /// <summary>
    /// A copy of the conversation to carry on differently: the whole of it, or up to one of its replies.
    /// The original is untouched. The copy is the operator's own session, also when the original was a helper's.
    /// </summary>
    private static async Task<IResult> ForkSession(string id, ForkSessionRequest? body, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (session is null) return Results.Json(new { error = "Session not found" }, statusCode: 404);

        var messages = ParseStoredMessages(session.Messages)
            .Select(m => System.Text.Json.Nodes.JsonNode.Parse(m.GetRawText()) as System.Text.Json.Nodes.JsonObject)
            .Where(m => m is not null).Select(m => m!).ToList();

        var whole = true;
        if (body?.Through is { } through)
        {
            var index = messages.FindIndex(m => UiMessages.IdOf(m) == through);
            // A fork ends where the agent had finished speaking: cut after a prompt, it would hold a question nobody answered.
            if (index < 0 || UiMessages.RoleOf(messages[index]) != "assistant")
            {
                return Results.Json(new { error = "through must be the id of one of the session's replies." }, statusCode: 400);
            }
            whole = index == messages.Count - 1;
            messages = messages.Take(index + 1).ToList();
        }

        string title;
        if (body?.Title is not null)
        {
            if (SessionTitler.CleanManual(body.Title) is not { } named)
            {
                return Results.Json(new { error = $"A title is one line of at most {SessionTitler.MaxManualLength} characters." }, statusCode: 400);
            }
            title = named;
        }
        else
        {
            const string suffix = " (fork)";
            var original = session.Title.EndsWith(suffix, StringComparison.Ordinal) ? session.Title[..^suffix.Length] : session.Title;
            title = (original.Length + suffix.Length <= SessionTitler.MaxManualLength ? original : original[..(SessionTitler.MaxManualLength - suffix.Length)]) + suffix;
        }

        // The summary stands in for messages up to a point; it is carried over only if the fork still contains that point.
        var summarised = session.SummarizedThrough is { } point && messages.Any(m => UiMessages.IdOf(m) == point);
        var fork = new Session
        {
            UserId = userId,
            Title = title,
            Messages = JsonSerializer.Serialize(messages, JsonOptions),
            Summary = summarised ? session.Summary : null,
            SummarizedThrough = summarised ? session.SummarizedThrough : null,
            // The task list is the agent's state at the end of the conversation; an earlier point never had this list.
            Tasks = whole ? session.Tasks : null,
        };
        db.Sessions.Add(fork);
        await db.SaveChangesAsync(ct);

        return Results.Json(new
        {
            fork.Id, fork.UserId, fork.Title, fork.CreatedAt, fork.UpdatedAt,
            messages = ParseStoredMessages(fork.Messages),
            summarizedThrough = fork.SummarizedThrough,
            tasks = Cerberus.Api.Chat.TaskList.Read(fork.Tasks),
            parentId = (string?)null,
            forkedFrom = session.Id,
        }, statusCode: 201);
    }

    /// <summary>The conversation as a Markdown file (see <see cref="SessionExport"/>).</summary>
    private static async Task<IResult> ExportSession(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var session = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (session is null) return Results.Json(new { error = "Session not found" }, statusCode: 404);

        var messages = ParseStoredMessages(session.Messages)
            .Select(m => System.Text.Json.Nodes.JsonNode.Parse(m.GetRawText()) as System.Text.Json.Nodes.JsonObject)
            .Where(m => m is not null).Select(m => m!).ToList();
        var markdown = SessionExport.Markdown(session, messages, DateTime.UtcNow);
        return Results.File(Encoding.UTF8.GetBytes(markdown), SessionExport.ContentType, SessionExport.FileName(session));
    }

    /// <summary>Names the session as the operator says.</summary>
    private static async Task<IResult> SetSessionTitle(string id, CreateSessionRequest body, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        if (SessionTitler.CleanManual(body.Title) is not { } title)
        {
            return Results.Json(new { error = $"A title is one line of at most {SessionTitler.MaxManualLength} characters." }, statusCode: 400);
        }
        var changed = await db.Sessions.Where(s => s.Id == id && s.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Title, title), ct);
        return changed == 0 ? Results.Json(new { error = "Session not found" }, statusCode: 404) : Results.Json(new { title });
    }

    private static async Task<IResult> CreateSession(CreateSessionRequest body, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        if (body.Title is null)
        {
            return Results.Json(new { error = "Invalid request body" }, statusCode: 400);
        }
        if (body.ParentId is not null)
        {
            // A helper's conversation hangs under one of the caller's own chats, and helpers have no helpers.
            var parent = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == body.ParentId && s.UserId == userId, ct);
            if (parent is null) return Results.Json(new { error = "Parent session not found" }, statusCode: 404);
            if (parent.ParentId is not null) return Results.Json(new { error = "A helper cannot start helpers of its own." }, statusCode: 400);
        }
        var session = new Session { UserId = userId, Title = SessionTitler.Initial(body.Title), ParentId = body.ParentId };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Results.Json(new
        {
            session.Id,
            session.UserId,
            session.Title,
            session.CreatedAt,
            session.UpdatedAt,
            messages = Array.Empty<object>(),
            parentId = session.ParentId,
        }, statusCode: 201);
    }

    // --- chat (AI-SDK UI message stream) ------------------------------------

    private static async Task StreamChat(
        UiChatRequest body, HttpContext http, CerberusDbContext db, ChatRunner runner,
        ApiKeyService keys, ChatCompactor compactor, ChatLimits limits, CancellationToken ct)
    {
        var userId = UserId(http);

        // Same request contract as the TypeScript server: id, at least one well-formed
        // message, a mode of BUILD or PLAN, and a supported model.
        var mode = body.Mode?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(body.Id) || body.Messages is not { Count: > 0 } ||
            !body.Messages.All(UiMessages.IsValid) || !ChatTools.IsValidMode(mode))
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = "Invalid request body" }, ct);
            return;
        }
        if (string.IsNullOrWhiteSpace(body.Model) || !ModelRegistry.IsSupported(body.Model))
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = $"Unsupported model: {body.Model}. Give {ModelRegistry.Accepted}." }, ct);
            return;
        }

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == body.Id && s.UserId == userId, ct);
        if (session is null)
        {
            http.Response.StatusCode = 404;
            await http.Response.WriteAsJsonAsync(new { error = "Session not found" }, ct);
            return;
        }

        // A helper only ever looks: whatever mode the request names, it gets the read-only tools.
        var helper = session.ParentId is not null;
        if (helper) mode = ChatTools.ModePlan;

        // Incoming messages replace stored ones with the same id, and are stamped with the mode and model in use.
        var messages = UiMessages.Merge(ParseStoredMessages(session.Messages), body.Messages);
        var incomingIds = body.Messages.Select(m => m.GetProperty("id").GetString()).ToHashSet();
        foreach (var message in messages.Where(m => incomingIds.Contains(UiMessages.IdOf(m))))
        {
            var metadata = message["metadata"] as System.Text.Json.Nodes.JsonObject ?? [];
            metadata["mode"] = mode;
            metadata["model"] = body.Model;
            message["metadata"] = metadata;
        }

        var credentials = await keys.ResolveCredentialsAsync(userId, ct);
        // What the operator asked to be remembered: their instructions and their notes about no target in particular.
        var memory = await Memory.PromptSectionAsync(db, userId, engagementId: null, targetPath: null, ct);

        // Compaction: a new user message on a conversation that is about to outgrow the model's window
        // has its earlier part summarised first. A reply in progress (tool results coming back) is never interrupted for it.
        var notices = new List<(string Kind, object Data)>();
        if (UiMessages.RoleOf(messages[^1]) == "user" &&
            ChatCompactor.IsDue(messages, session.Summary, session.SummarizedThrough, body.Model, limits))
        {
            try
            {
                if (await compactor.CompactAsync(messages, session.Summary, session.SummarizedThrough, body.Model, credentials, ct) is { } compaction)
                {
                    session.Summary = compaction.Summary;
                    session.SummarizedThrough = compaction.Through;
                    await db.SaveChangesAsync(ct);
                    notices.Add(("compaction", new { messages = compaction.Messages }));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The reply is attempted anyway; if the conversation really is too long, the provider will say so.
                http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Chat").LogWarning(ex, "Could not compact session {SessionId}.", session.Id);
            }
        }

        UiMessageStreamWriter.ApplyHeaders(http.Response);
        var result = await runner.RunAsync(
            new UiMessageStreamWriter(http.Response),
            new ChatRun(mode!, body.Model, messages, credentials, memory, ChatContext.Read(body.Context).PromptSection(),
                session.Summary, session.SummarizedThrough, notices, session.Tasks,
                Delegation: body.Subagents == true, Helper: helper), ct);

        // The task list is kept as soon as it changes — also when the reply is waiting on client tools:
        // the continuation is a new request and must find the list as the model left it.
        if (result.Tasks is not null)
        {
            session.Tasks = result.Tasks;
            if (!result.Complete) await db.SaveChangesAsync(CancellationToken.None);
        }

        // Stored only once the reply is whole. While tool calls are waiting for the
        // client, the client holds the message and sends it back with the results.
        if (result.Complete)
        {
            session.Messages = JsonSerializer.Serialize(result.Messages, JsonOptions);
            session.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    // --- helpers ------------------------------------------------------------

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static List<JsonElement> ParseStoredMessages(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
            }
        }
        catch (JsonException)
        {
            // fall through
        }
        return [];
    }
}
