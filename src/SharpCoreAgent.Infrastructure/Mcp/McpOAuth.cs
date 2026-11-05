using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// Runs the OAuth 2.1 authorization-code flow with PKCE, using a loopback callback.
/// </summary>
/// <remarks>
/// The developer opens the printed authorization URL; this type never starts a browser, because launching one would be
/// a third <c>Process.Start</c> outside the shell tool and the MCP client. The callback listener is bound to
/// <c>127.0.0.1</c> on an ephemeral port, the state is checked before the code is exchanged, and the access token lives
/// only in the returned string. Discovery, dynamic registration, refresh tokens and persistence are deliberately not
/// implemented here.
/// </remarks>
public sealed class McpOAuth
{
    private readonly McpOAuthOptions _options;
    private readonly HttpMessageHandler? _handler;

    /// <summary>Creates the flow over the production HTTP handler.</summary>
    /// <param name="options">The explicit OAuth settings.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public McpOAuth(McpOAuthOptions options)
        : this(options, handler: null)
    {
    }

    /// <summary>Creates the flow over a supplied handler, for tests and embedders.</summary>
    /// <param name="options">The explicit OAuth settings.</param>
    /// <param name="handler">The token-exchange handler, or null for the production handler.</param>
    internal McpOAuth(McpOAuthOptions options, HttpMessageHandler? handler)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        _handler = handler;
    }

    /// <summary>
    /// Runs the flow and returns the access token.
    /// </summary>
    /// <param name="showUrl">Receives the authorization URL the developer must open.</param>
    /// <param name="cancellationToken">Cancels the wait for the callback.</param>
    /// <returns>The access token, exactly as the token endpoint returned it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="showUrl"/> is null.</exception>
    /// <exception cref="McpOAuthException">The flow was refused, timed out or returned no token.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task<string> AuthorizeAsync(Action<string> showUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(showUrl);

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));
        var (listener, redirectUri) = StartListener();

        try
        {
            var authorizationUrl = BuildAuthorizationUrl(redirectUri, challenge, state);
            showUrl(authorizationUrl.ToString());

            var code = await WaitForCodeAsync(listener, state, cancellationToken).ConfigureAwait(false);
            return await ExchangeAsync(code, verifier, redirectUri, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            listener.Close();
        }
    }
 
    private static (HttpListener Listener, Uri RedirectUri) StartListener()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var prefix = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/");
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        return (listener, new Uri(new Uri(prefix), "callback"));
    }

    private Uri BuildAuthorizationUrl(Uri redirectUri, string challenge, string state)
    {
        var pairs = new List<string>
        {
            "response_type=code",
            $"client_id={Escape(_options.ClientId)}",
            $"redirect_uri={Escape(redirectUri.AbsoluteUri)}",
            $"code_challenge={Escape(challenge)}",
            "code_challenge_method=S256",
            $"state={Escape(state)}",
        };

        if (!string.IsNullOrWhiteSpace(_options.Scope))
        {
            pairs.Add($"scope={Escape(_options.Scope)}");
        }

        var builder = new UriBuilder(_options.AuthorizationEndpoint);
        var existing = builder.Query.TrimStart('?');
        builder.Query = existing.Length == 0
            ? string.Join('&', pairs)
            : $"{existing}&{string.Join('&', pairs)}";
        return builder.Uri;
    }

    private async Task<string> WaitForCodeAsync(
        HttpListener listener,
        string expectedState,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.Timeout);

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new McpOAuthException("the OAuth callback was not received before the timeout");
            }

            if (!string.Equals(context.Request.Url?.AbsolutePath, "/callback", StringComparison.Ordinal))
            {
                await WriteResponseAsync(context.Response, HttpStatusCode.NotFound, "Not the OAuth callback.").ConfigureAwait(false);
                continue;
            }

            var query = context.Request.QueryString;
            var state = query["state"];
            var error = query["error"];
            var code = query["code"];

            if (!string.IsNullOrEmpty(error))
            {
                await WriteResponseAsync(context.Response, HttpStatusCode.BadRequest, "Authorization was refused.").ConfigureAwait(false);
                throw new McpOAuthException($"the authorization server refused the request: {error}");
            }

            if (!string.Equals(state, expectedState, StringComparison.Ordinal))
            {
                await WriteResponseAsync(context.Response, HttpStatusCode.BadRequest, "The OAuth state did not match.").ConfigureAwait(false);
                throw new McpOAuthException("the OAuth state did not match the request this client started");
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                await WriteResponseAsync(context.Response, HttpStatusCode.BadRequest, "No authorization code was returned.").ConfigureAwait(false);
                throw new McpOAuthException("the OAuth callback returned no authorization code");
            }

            await WriteResponseAsync(context.Response, HttpStatusCode.OK, "Authorization complete. You may close this window.").ConfigureAwait(false);
            return code;
        }
    }

 
    private async Task<string> ExchangeAsync(
        string code,
        string verifier,
        Uri redirectUri,
        CancellationToken cancellationToken)
    {
        using var client = _handler is null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: true)
            : new HttpClient(_handler, disposeHandler: true);
        client.Timeout = Timeout.InfiniteTimeSpan;

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>("redirect_uri", redirectUri.AbsoluteUri),
                new KeyValuePair<string, string>("client_id", _options.ClientId),
                new KeyValuePair<string, string>("code_verifier", verifier),
            ]),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.Timeout);
        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new McpOAuthException(string.Create(
                CultureInfo.InvariantCulture,
                $"the OAuth token endpoint answered HTTP {(int)response.StatusCode}: {Trim(body, 300)}"));
        }

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("access_token", out var token) ||
            token.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(token.GetString()))
        {
            throw new McpOAuthException("the OAuth token endpoint returned no access token");
        }

        return token.GetString()!;
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response, HttpStatusCode status, string message)
    {
        var bytes = Encoding.UTF8.GetBytes($"<!doctype html><title>SharpCoreAgent</title><p>{WebUtility.HtmlEncode(message)}</p>");
        response.StatusCode = (int)status;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Trim(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";
}

