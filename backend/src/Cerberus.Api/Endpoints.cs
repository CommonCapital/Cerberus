using System.Text;
using System.Text.Json;
using Cerberus.Api.Auth;
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
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));

        var auth = app.MapGroup("/auth");
        auth.MapPost("/register", Register);
        auth.MapPost("/login", Login);
        auth.MapPost("/logout", Logout);

        var sessions = app.MapGroup("/sessions").AddEndpointFilter<RequireAuthFilter>();
        sessions.MapGet("/", ListSessions);
        sessions.MapGet("/{id}", GetSession);
        sessions.MapPost("/", CreateSession);

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
        CatalogLoader loader, ComplianceEngine engine, ScanService scanner, ScaService sca, CancellationToken ct)
    {
        var userId = UserId(http);
        var catalogId = string.IsNullOrWhiteSpace(body.CatalogId) ? "enterprise-sap" : body.CatalogId!;

        var catalog = loader.Get(catalogId);
        if (catalog is null)
        {
            return Results.Json(new { error = $"Unknown catalog '{catalogId}'" }, statusCode: 400);
        }

        // Findings come from a persisted scan (preferred) or a fresh scan of a path.
        IReadOnlyList<Finding> findings;
        string targetPath;
        string? scanId = null;
        bool dependencyCheckCompleted;

        if (!string.IsNullOrWhiteSpace(body.ScanId))
        {
            var scan = await db.Scans.FirstOrDefaultAsync(s => s.Id == body.ScanId && s.UserId == userId, ct);
            if (scan is null) return Results.Json(new { error = "Scan not found" }, statusCode: 404);
            findings = DeserializeFindings(scan.Findings);
            targetPath = scan.TargetPath;
            scanId = scan.Id;
            dependencyCheckCompleted = scan.ScaCompleted;
        }
        else
        {
            targetPath = string.IsNullOrWhiteSpace(body.Path) ? "." : body.Path!;
            try
            {
                var staticFindings = scanner.Scan(targetPath).Findings;
                var scaResult = (body.Sca ?? true)
                    ? await sca.ScanAsync(targetPath, ct)
                    : new ScaResult(0, 0, []);
                findings = staticFindings.Concat(scaResult.Findings).ToList();
                dependencyCheckCompleted = scaResult.Completed;
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }
        }

        var report = engine.Evaluate(catalog, findings, dependencyCheckCompleted);

        var assessment = new ComplianceAssessment
        {
            UserId = userId,
            CatalogId = report.CatalogId,
            TargetPath = targetPath,
            ScanId = scanId,
            TotalControls = report.TotalControls,
            Compliant = report.Compliant,
            NonCompliant = report.NonCompliant,
            NotAssessed = report.NotAssessed,
            Score = report.Score,
            Matrix = JsonSerializer.Serialize(report.Controls, JsonOptions),
        };
        db.ComplianceAssessments.Add(assessment);
        await db.SaveChangesAsync(ct);

        return Results.Json(new
        {
            id = assessment.Id,
            catalogId = report.CatalogId,
            catalogName = report.CatalogName,
            catalogVersion = report.CatalogVersion,
            targetPath,
            scanId,
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
                a.Id, a.CatalogId, a.TargetPath, a.ScanId,
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
            }),
        }, JsonOptions);
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
        ScanService scanner, ScaService sca, CancellationToken ct)
    {
        var userId = UserId(http);
        var targetPath = string.IsNullOrWhiteSpace(body.Path) ? "." : body.Path!;

        ScanResult result;
        ScaResult scaResult;
        try
        {
            // Static (regex) pass is synchronous; SCA is an async OSV lookup.
            result = scanner.Scan(targetPath);
            scaResult = (body.Sca ?? true)
                ? await sca.ScanAsync(targetPath, ct)
                : new ScaResult(0, 0, []);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 400);
        }

        // Merge both passes; re-sort by severity so the combined set is ranked.
        var findings = result.Findings.Concat(scaResult.Findings)
            .OrderByDescending(f => f.Severity)
            .ToList();

        var findingsJson = JsonSerializer.Serialize(findings, JsonOptions);
        var scan = new Scan
        {
            UserId = userId,
            TargetPath = targetPath,
            FilesScanned = result.FilesScanned,
            FindingCount = findings.Count,
            ScaCompleted = scaResult.Completed,
            Findings = findingsJson,
        };
        db.Scans.Add(scan);
        await db.SaveChangesAsync(ct);

        return Results.Json(new
        {
            id = scan.Id,
            targetPath = scan.TargetPath,
            createdAt = scan.CreatedAt,
            filesScanned = result.FilesScanned,
            manifestsScanned = scaResult.ManifestsScanned,
            dependenciesScanned = scaResult.DependenciesScanned,
            scaCompleted = scaResult.Completed,
            findingCount = findings.Count,
            findings,
        }, JsonOptions);
    }

    private static async Task<IResult> ListScans(HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var scans = await db.Scans
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.Id, s.TargetPath, s.FilesScanned, s.FindingCount, s.ScaCompleted, s.CreatedAt })
            .ToListAsync(ct);
        return Results.Json(scans);
    }

    private static async Task<IResult> GetScan(string id, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var scan = await db.Scans.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId, ct);
        if (scan is null) return Results.Json(new { error = "Scan not found" }, statusCode: 404);

        return Results.Json(new
        {
            scan.Id,
            scan.TargetPath,
            scan.FilesScanned,
            scan.FindingCount,
            scan.ScaCompleted,
            scan.CreatedAt,
            findings = ParseStoredMessages(scan.Findings),
        });
    }

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

    private static async Task<IResult> Login(CredentialsRequest body, AuthService auth, CancellationToken ct)
    {
        if (!IsValid(body))
        {
            return Results.Json(new { error = "A valid email and a password (min 6 characters) are required" }, statusCode: 400);
        }
        var token = await auth.LoginAsync(body.Email, body.Password, ct);
        return token is null
            ? Results.Json(new { error = "Invalid email or password" }, statusCode: 401)
            : Results.Json(new { token });
    }

    private static async Task<IResult> Logout(HttpContext http, AuthService auth, CancellationToken ct)
    {
        var token = RequireAuthFilter.ExtractBearerToken(http.Request.Headers.Authorization);
        if (token is not null) await auth.LogoutAsync(token, ct);
        return Results.Json(new { success = true });
    }

    private static bool IsValid(CredentialsRequest body) =>
        !string.IsNullOrWhiteSpace(body.Email)
        && body.Email.Contains('@')
        && body.Password?.Length >= 6;

    // --- sessions -----------------------------------------------------------

    private static async Task<IResult> ListSessions(HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var sessions = await db.Sessions
            .Where(s => s.UserId == userId)
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
        });
    }

    private static async Task<IResult> CreateSession(CreateSessionRequest body, HttpContext http, CerberusDbContext db, CancellationToken ct)
    {
        var userId = UserId(http);
        var session = new Session { UserId = userId, Title = body.Title };
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
        }, statusCode: 201);
    }

    // --- chat (AI-SDK UI message stream) ------------------------------------

    private static async Task StreamChat(
        UiChatRequest body, HttpContext http, CerberusDbContext db, IModelProviderResolver resolver,
        ApiKeyService keys, CancellationToken ct)
    {
        var userId = UserId(http);

        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == body.Id && s.UserId == userId, ct);
        if (session is null)
        {
            http.Response.StatusCode = 404;
            await http.Response.WriteAsJsonAsync(new { error = "Session not found" }, ct);
            return;
        }

        if (!ModelRegistry.IsSupported(body.Model))
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = $"Unsupported model: {body.Model}" }, ct);
            return;
        }

        var history = ParseStoredMessages(session.Messages);
        var promptMessages = BuildPromptMessages(history, body.Messages);
        var system = SystemPrompt.Build(body.Mode);

        UiMessageStreamWriter.ApplyHeaders(http.Response);
        var writer = new UiMessageStreamWriter(http.Response);

        var assistantId = Guid.NewGuid().ToString("n");
        const string textId = "0";
        var startedAt = DateTime.UtcNow;
        var assistant = new StringBuilder();

        await writer.StartAsync(assistantId, new { mode = body.Mode, model = body.Model }, ct);
        await writer.StartStepAsync(ct);

        try
        {
            var provider = resolver.Resolve(body.Model);
            var credentials = await keys.ResolveCredentialsAsync(userId, ct);
            var completion = new ChatCompletionRequest(body.Model, promptMessages, system, credentials);

            await writer.TextStartAsync(textId, ct);
            await foreach (var delta in provider.StreamAsync(completion, ct))
            {
                assistant.Append(delta);
                await writer.TextDeltaAsync(textId, delta, ct);
            }
            await writer.TextEndAsync(textId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await writer.ErrorAsync(ex.Message, ct);
            await writer.DoneAsync(ct);
            return;
        }

        await writer.FinishStepAsync(ct);
        var durationMs = (long)(DateTime.UtcNow - startedAt).TotalMilliseconds;
        await writer.FinishAsync(new { mode = body.Mode, model = body.Model, durationMs }, ct);

        await PersistAsync(db, session, history, body, assistantId, assistant.ToString(), durationMs, ct);

        await writer.DoneAsync(ct);
    }

    private static async Task PersistAsync(
        CerberusDbContext db, Session session, List<JsonElement> history, UiChatRequest body,
        string assistantId, string assistantText, long durationMs, CancellationToken ct)
    {
        var merged = new List<object>();
        foreach (var h in history) merged.Add(h);
        foreach (var m in body.Messages)
        {
            merged.Add(new
            {
                id = m.Id,
                role = m.Role,
                parts = m.Parts.Select(p => new { type = p.Type, text = p.Text }),
            });
        }
        merged.Add(new
        {
            id = assistantId,
            role = "assistant",
            parts = new[] { new { type = "text", text = assistantText } },
            metadata = new { mode = body.Mode, model = body.Model, durationMs },
        });

        session.Messages = JsonSerializer.Serialize(merged, JsonOptions);
        session.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // --- helpers ------------------------------------------------------------

    private static string UserId(HttpContext http) => (string)http.Items[RequireAuthFilter.UserIdItem]!;

    private static IReadOnlyList<Finding> DeserializeFindings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Finding>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

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

    private static List<ChatMessage> BuildPromptMessages(List<JsonElement> history, List<UiMessage> incoming)
    {
        var result = new List<ChatMessage>();
        foreach (var h in history)
        {
            var text = ExtractText(h);
            if (!string.IsNullOrEmpty(text)) result.Add(new ChatMessage(RoleOf(h), text));
        }
        foreach (var m in incoming)
        {
            var text = string.Concat(m.Parts.Where(p => p.Type == "text").Select(p => p.Text));
            if (!string.IsNullOrEmpty(text)) result.Add(new ChatMessage(m.Role, text));
        }
        return result;
    }

    private static string ExtractText(JsonElement message)
    {
        if (!message.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }
        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                sb.Append(text.GetString());
            }
        }
        return sb.ToString();
    }

    private static string RoleOf(JsonElement message) =>
        message.TryGetProperty("role", out var role) ? role.GetString() ?? "user" : "user";
}
