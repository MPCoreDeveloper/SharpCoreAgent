namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// The OAuth settings for one remote MCP invocation.
/// </summary>
/// <remarks>
/// The endpoints and client id are optional because T035d can discover them from the remote resource or an explicit
/// issuer, and can register a public client dynamically. The redirect is a loopback URI chosen at run time, which is
/// why no redirect URI appears here. A token may be persisted only when <see cref="TokenFile"/> is named.
/// </remarks>
public sealed record McpOAuthOptions
{
    /// <summary>Gets the public client identifier, or null when dynamic registration should obtain one.</summary>
    public string? ClientId { get; init; }

    /// <summary>Gets the authorization endpoint, or null when discovery supplies it.</summary>
    public Uri? AuthorizationEndpoint { get; init; }

    /// <summary>Gets the token endpoint, or null when discovery supplies it.</summary>
    public Uri? TokenEndpoint { get; init; }

    /// <summary>Gets the registration endpoint, or null when discovery supplies it.</summary>
    public Uri? RegistrationEndpoint { get; init; }

    /// <summary>Gets the authorization server issuer to discover metadata from.</summary>
    public Uri? Issuer { get; init; }

    /// <summary>Gets the protected resource URL to discover the authorization server from.</summary>
    public Uri? Resource { get; init; }

    /// <summary>Gets the optional space-separated scopes.</summary>
    public string? Scope { get; init; }

    /// <summary>Gets how long the callback waits for the browser. Default two minutes.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets the optional file where the access and refresh tokens are persisted.</summary>
    public string? TokenFile { get; init; }

    /// <summary>
    /// Checks the settings and returns this instance.
    /// </summary>
    /// <returns>This instance.</returns>
    /// <exception cref="ArgumentException">A value is blank or an endpoint is not allowed.</exception>
    public McpOAuthOptions Validate()
    {
        RequireEndpoint(AuthorizationEndpoint, nameof(AuthorizationEndpoint));
        RequireEndpoint(TokenEndpoint, nameof(TokenEndpoint));
        RequireEndpoint(RegistrationEndpoint, nameof(RegistrationEndpoint));
        RequireEndpoint(Issuer, nameof(Issuer));
        RequireEndpoint(Resource, nameof(Resource));

        if (AuthorizationEndpoint is null && TokenEndpoint is null &&
            Issuer is null && Resource is null)
        {
            throw new ArgumentException("an OAuth endpoint, issuer or resource is required");
        }

        if ((AuthorizationEndpoint is null) != (TokenEndpoint is null))
        {
            throw new ArgumentException("the OAuth authorization and token endpoints must be named together");
        }

        if (Timeout < TimeSpan.FromSeconds(10) || Timeout > TimeSpan.FromMinutes(30))
        {
            throw new ArgumentException("the OAuth callback timeout must be between 10 seconds and 30 minutes");
        }

        if (TokenFile is { Length: 0 })
        {
            throw new ArgumentException("the OAuth token file must not be empty");
        }

        return this;
    }

    private static void RequireEndpoint(Uri? endpoint, string name)
    {
        if (endpoint is null)
        {
            return;
        }

        if (endpoint.Scheme != Uri.UriSchemeHttps &&
            !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
        {
            throw new ArgumentException("the OAuth endpoint must be https, or http on a loopback host", name);
        }
    }
}
