namespace Cerberus.Api;

public sealed record CredentialsRequest(string Email, string Password);

public sealed record CreateSessionRequest(string Title);

public sealed record SetApiKeyRequest(string Provider, string ApiKey);

/// <summary>
/// A Phase-1 scan request. <paramref name="Sca"/> toggles dependency/CVE
/// analysis via OSV.dev (defaults on); set false for fully-offline runs.
/// </summary>
public sealed record ScanRequest(string? Path, bool? Sca = true);

/// <summary>
/// Request a Phase-2 compliance assessment. Either reuse an existing scan's
/// findings (<see cref="ScanId"/>) or run a fresh scan over <see cref="Path"/>.
/// </summary>
public sealed record ComplianceRequest(string? CatalogId, string? ScanId, string? Path);

public sealed record ChatRequestMessage(string Role, string Content);

// Used by the SignalR hub skeleton.
public sealed record ChatRequest(
    string SessionId,
    string Model,
    string? System,
    List<ChatRequestMessage> Messages);

// --- AI-SDK UI message shapes (CLI-compatible /chat) -----------------------
// Mirrors @ai-sdk UIMessage: a message has `parts`, each part has a `type`
// and, for text parts, a `text`. Extra part fields are ignored on read.
public sealed record UiPart(string Type, string? Text);

public sealed record UiMessage(string Id, string Role, List<UiPart> Parts);

public sealed record UiChatRequest(
    string Id,
    List<UiMessage> Messages,
    string Mode,
    string Model);
