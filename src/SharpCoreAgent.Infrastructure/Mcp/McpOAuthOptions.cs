namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// The explicit OAuth settings for one remote MCP invocation.
/// </summary>
/// <remarks>
/// The developer names the authorization server and the public client; this client discovers nothing and registers
/// nothing. The redirect is a loopback URI chosen at run time, which is why no redirect URI appears here. A token is
/// held in memory for the invocation only.
/// </remarks>
public sealed record McpOAuthOptions
{
    /// <summary>Gets the public client identifier.</summary>
    public required string ClientId { get; init; }

    /// <summary>Gets the authorization endpoint the browser opens.</summary>
    public required Uri AuthorizationEndpoint { get; init; }

    /// <summary>Gets the token endpoint the code is exchanged at.</summary>
    public required Uri TokenEndpoint { get; init; }

    /// <summary>Gets the optional space-separated scopes.</summary>
    public string? Scope { get; init; }

    /// <summary>Gets how long the callback waits for the browser. Default two minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Checks the settings and returns this instance.
    /// </summary>
    /// <returns>This instance.</returns>
    /// <exception cref="ArgumentException">A required value is blank or an endpoint is not allowed.</exception>
    public McpOAuthOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new ArgumentException("an OAuth client id is required");
        }

        RequireEndpoint(AuthorizationEndpoint, nameof(AuthorizationEndpoint));
        RequireEndpoint(TokenEndpoint, nameof(TokenEndpoint));
        if (Timeout < TimeSpan.FromSeconds(10) || Timeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentException("the OAuth callback timeout must be between 10 seconds and 30 minutes");
        }

        return this;
    }

    private static void RequireEndpoint(Uri endpoint, string name)
    {
        if (endpoint is null)
        {
            throw new ArgumentException("the OAuth endpoint is required", name);
        }

        if (endpoint.Scheme != Uri.UriSchemeHttps &&
            !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
        {
            throw new ArgumentException("the OAuth endpoint must be https, or http on a loopback host", name);
        }
    }
}
