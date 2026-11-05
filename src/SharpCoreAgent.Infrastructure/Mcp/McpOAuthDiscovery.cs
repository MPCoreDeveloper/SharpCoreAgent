using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// The OAuth metadata endpoints discovery found.
/// </summary>
/// <param name="AuthorizationEndpoint">Where the browser authorizes.</param>
/// <param name="TokenEndpoint">Where the code or refresh token is exchanged.</param>
/// <param name="RegistrationEndpoint">Where a public client is registered, when advertised.</param>
internal sealed record McpOAuthMetadata(
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    Uri? RegistrationEndpoint);

/// <summary>
/// Discovers the authorization server for a protected MCP resource, then its metadata endpoints.
/// </summary>
/// <remarks>
/// This implements the two documents the MCP authorization specification points at: protected-resource metadata
/// (<c>/.well-known/oauth-protected-resource</c>) and authorization-server metadata
/// (<c>/.well-known/oauth-authorization-server</c>). It does not implement OpenID discovery or a registry of issuers;
/// a document that names no usable endpoint is a clear failure rather than a fallback to an unrelated host.
/// </remarks>
internal static class McpOAuthDiscovery
{
    /// <summary>Discovers metadata from an explicit issuer, or from the resource's advertised issuer.</summary>
    /// <param name="options">The settings naming an issuer or resource.</param>
    /// <param name="handler">The HTTP handler, or null for production.</param>
    /// <param name="cancellationToken">Cancels discovery.</param>
    /// <returns>The discovered endpoints.</returns>
    /// <exception cref="McpOAuthException">The metadata is absent or malformed.</exception>
    public static async Task<McpOAuthMetadata> DiscoverAsync(
        McpOAuthOptions options,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        var issuer = options.Issuer ?? await IssuerForResourceAsync(options.Resource!, handler, cancellationToken)
            .ConfigureAwait(false);
        var metadataUrl = new Uri(issuer.AbsoluteUri.TrimEnd('/') + "/.well-known/oauth-authorization-server");
        using var document = await GetAsync(metadataUrl, handler, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        var authorization = ReadEndpoint(root, "authorization_endpoint")
            ?? throw new McpOAuthException("the OAuth metadata names no authorization endpoint");
        var token = ReadEndpoint(root, "token_endpoint")
            ?? throw new McpOAuthException("the OAuth metadata names no token endpoint");
        var registration = ReadEndpoint(root, "registration_endpoint");

        return new McpOAuthMetadata(authorization, token, registration);
    }

    private static async Task<Uri> IssuerForResourceAsync(
        Uri resource,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        var metadataUrl = new Uri(resource.GetLeftPart(UriPartial.Authority) + "/.well-known/oauth-protected-resource");
        using var document = await GetAsync(metadataUrl, handler, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.TryGetProperty("authorization_servers", out var servers) &&
            servers.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in servers.EnumerateArray())
            {
                if (server.ValueKind == JsonValueKind.String &&
                    Uri.TryCreate(server.GetString(), UriKind.Absolute, out var issuer))
                {
                    return Validate(issuer, "authorization_servers");
                }
            }
        }

        throw new McpOAuthException("the protected-resource metadata names no authorization server");
    }
 
    private static async Task<JsonDocument> GetAsync(
        Uri uri,
        HttpMessageHandler? handler,
        CancellationToken cancellationToken)
    {
        using var client = handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: true);
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new McpOAuthException(string.Create(
                CultureInfo.InvariantCulture,
                $"OAuth metadata at {uri} answered HTTP {(int)response.StatusCode}"));
        }

        var body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new McpOAuthException($"OAuth metadata at {uri} is not JSON: {ex.Message}");
        }
    }

    private static Uri? ReadEndpoint(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return Uri.TryCreate(value.GetString(), UriKind.Absolute, out var endpoint)
            ? Validate(endpoint, name)
            : throw new McpOAuthException($"the OAuth metadata's {name} is not an absolute URL");
    }

    private static Uri Validate(Uri endpoint, string name)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps &&
            !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback))
        {
            throw new McpOAuthException($"the OAuth metadata's {name} must be https, or http on loopback");
        }

        return endpoint;
    }
}

