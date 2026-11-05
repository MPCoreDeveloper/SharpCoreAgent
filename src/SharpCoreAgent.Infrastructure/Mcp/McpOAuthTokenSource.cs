using System.Globalization;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// Keeps the current access token fresh for a running turn.
/// </summary>
/// <remarks>
/// The source deliberately refreshes only when a token is close to expiry (thirty seconds of slack) or already
/// expired, so ordinary calls cost no token-endpoint round trip. A refresh token is required once the access token is
/// stale; without one the source returns the stale token and lets the remote server answer 401, which is the honest
/// signal that the developer must authorize again. The refreshed token is written to the same explicit token file when
/// one was configured.
/// </remarks>
public sealed class McpOAuthTokenSource : IDisposable
{
    private readonly McpOAuth _flow;
    private readonly string? _tokenFile;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpOAuthToken _token;

    /// <summary>Creates a source around the token one flow produced.</summary>
    /// <param name="flow">The flow used for refreshes.</param>
    /// <param name="token">The initial token.</param>
    /// <param name="tokenFile">The token file to update, or null.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public McpOAuthTokenSource(McpOAuth flow, McpOAuthToken token, string? tokenFile = null)
    {
        _flow = flow ?? throw new ArgumentNullException(nameof(flow));
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _tokenFile = tokenFile;
    }

    /// <summary>Gets the current access token, refreshing it when it is near expiry.</summary>
    /// <param name="cancellationToken">Cancels a refresh.</param>
    /// <returns>The bearer token.</returns>
    /// <exception cref="McpOAuthException">The token expired and no refresh token was available.</exception>
    public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_token.ExpiresAt is null || _token.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return _token.AccessToken;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_token.ExpiresAt is null || _token.ExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30))
            {
                return _token.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(_token.RefreshToken))
            {
                throw new McpOAuthException("the OAuth access token expired and no refresh token is available");
            }

            _token = await _flow.RefreshAsync(_token, cancellationToken).ConfigureAwait(false);
            if (_tokenFile is { Length: > 0 } path)
            {
                await McpOAuthTokenStore
                    .SaveAsync(path, McpStoredToken.FromToken(_token), cancellationToken)
                    .ConfigureAwait(false);
            }

            return _token.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gets the current token without refreshing it.</summary>
    public McpOAuthToken Current => _token;

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    internal static string Describe(McpOAuthToken token) => string.Create(
        CultureInfo.InvariantCulture,
        $"OAuth token expires {token.ExpiresAt?.ToString("O", CultureInfo.InvariantCulture) ?? "unknown"}");
}
