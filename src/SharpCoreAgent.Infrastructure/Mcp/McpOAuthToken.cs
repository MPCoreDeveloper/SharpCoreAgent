namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// One OAuth token result, including the refresh material needed by a long-running turn.
/// </summary>
/// <param name="AccessToken">The bearer token.</param>
/// <param name="RefreshToken">The refresh token, when issued.</param>
/// <param name="ExpiresAt">The access-token expiry, when the server reported one.</param>
/// <param name="ClientId">The public client the token belongs to.</param>
public sealed record McpOAuthToken(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? ClientId);
