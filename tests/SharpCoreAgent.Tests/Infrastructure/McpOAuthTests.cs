using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The OAuth authorization-code flow: PKCE, state, the loopback callback and the token exchange.
/// </summary>
public sealed class McpOAuthTests
{
    [Fact]
    public async Task TheCodeIsExchangedWithPkceAfterAValidatedCallback()
    {
        string? verifier = null;
        var handler = new TokenHandler(request =>
        {
            var form = request.Content!.ReadAsStringAsync(Token).GetAwaiter().GetResult();
            verifier = FormValue(form, "code_verifier");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"access_token":"access-42","token_type":"Bearer"}""", Encoding.UTF8, "application/json"),
            };
        });

        var flow = new McpOAuth(
            new McpOAuthOptions
            {
                ClientId = "sharp",
                AuthorizationEndpoint = new Uri("https://id.example/authorize"),
                TokenEndpoint = new Uri("https://id.example/token"),
                Scope = "mcp.read",
                Timeout = TimeSpan.FromSeconds(10),
            },
            handler);

        var authorizationUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = flow.AuthorizeAsync(
            url =>
            {
                authorizationUrl.SetResult(url);
            },
            Token);

        var url = new Uri(await authorizationUrl.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        var query = Query(url);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        Assert.Equal("mcp.read", query["scope"]);

        using var client = new HttpClient();
        var callback = $"{query["redirect_uri"]}?code=code-1&state={Uri.EscapeDataString(query["state"])}";
        using var response = await client.GetAsync(callback, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("access-42", await pending.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.False(string.IsNullOrWhiteSpace(verifier));
    }

    [Fact]
    public async Task AMismatchedStateEndsTheFlow()
    {
        var handler = new TokenHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var flow = new McpOAuth(
            new McpOAuthOptions
            {
                ClientId = "sharp",
                AuthorizationEndpoint = new Uri("https://id.example/authorize"),
                TokenEndpoint = new Uri("https://id.example/token"),
                Timeout = TimeSpan.FromSeconds(10),
            },
            handler);

        var authorizationUrl = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = flow.AuthorizeAsync(url => authorizationUrl.SetResult(url), Token);
        var url = new Uri(await authorizationUrl.Task.WaitAsync(TimeSpan.FromSeconds(10), Token));
        var query = Query(url);

        using var client = new HttpClient();
        using var response = await client.GetAsync(
            $"{query["redirect_uri"]}?code=code-1&state=wrong",
            Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await Assert.ThrowsAsync<McpOAuthException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(10), Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Dictionary<string, string> Query(Uri url) =>
        url.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));

    private static string? FormValue(string form, string name) =>
        form.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2 && Uri.UnescapeDataString(pair[0]) == name)
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .FirstOrDefault();

    private sealed class TokenHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
