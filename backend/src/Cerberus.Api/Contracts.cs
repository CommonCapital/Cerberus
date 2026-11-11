namespace Cerberus.Api;

public sealed record CredentialsRequest(string Email, string Password);

public sealed record CreateSessionRequest(string Title);

public sealed record SetApiKeyRequest(string Provider, string ApiKey);

/// <summary>
/// A Phase-1 scan request. <paramref name="Sca"/> toggles dependency/CVE
/// analysis via OSV.dev (defaults on); set false for fully-offline runs.
/// </summary>
public sealed record ScanRequest(string? Path, bool? Sca = true, string? EngagementId = null, string? Exclusions = null);

/// <summary>
/// Request a Phase-2 compliance assessment. Either reuse an existing scan's
/// findings (<see cref="ScanId"/>) or run a fresh scan over <see cref="Path"/>.
/// <paramref name="Sca"/> toggles dependency analysis for a fresh scan (defaults
/// on); with it off, dependency-mapped controls are reported as not assessed.
/// </summary>
public sealed record ComplianceRequest(
    string? CatalogId, string? ScanId, string? Path, bool? Sca = true, string? EngagementId = null);

/// <summary>An operator's answer for one control of an assessment.</summary>
public sealed record ControlResponseRequest(string? Verdict, string? Justification, string? ActionPlan, int? TimeLimitDays);

public sealed record ChatRequestMessage(string Role, string Content);

// Used by the SignalR chat hub skeleton.
public sealed record HubChatRequest(
    string SessionId,
    string Model,
    string? System,
    List<ChatRequestMessage> Messages);

// --- AI-SDK UI message request (CLI-compatible /chat) ----------------------
// Messages are kept as raw JSON: a UI message's parts include tool calls with
// their inputs, states and outputs, which must survive the round trip intact.
public sealed record UiChatRequest(
    string? Id,
    List<System.Text.Json.JsonElement>? Messages,
    string? Mode,
    string? Model);
