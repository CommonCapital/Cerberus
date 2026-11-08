namespace Cerberus.Api;

public sealed record CredentialsRequest(string Email, string Password);

public sealed record CreateSessionRequest(string Title);

public sealed record ChatRequestMessage(string Role, string Content);

public sealed record ChatRequest(
    string SessionId,
    string Model,
    string? System,
    List<ChatRequestMessage> Messages);
