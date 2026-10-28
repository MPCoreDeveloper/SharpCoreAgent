using System.Net;
using System.Net.Sockets;
using System.Text;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using SharpCoreAgent.Infrastructure.Web;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// <c>fetch_url</c> is tested entirely against a stub handler: no test in this file opens a socket, so the suite behaves
/// the same on a laptop behind a proxy, on a train, and in CI. What is pinned here is the contract a model and a
/// developer both depend on — what the request carries, what the result says, and what is refused before anything is
/// sent.
/// </summary>
public sealed class FetchUrlToolTests
{
    private const string Url = "https://example.com/page";

    [Fact]
    public async Task ReadsAPlainTextPageAndNamesWhatItRead()
    {
        var handler = new StubHandler().Map(Url, () => Respond("hello", "text/plain"));
        var result = await InvokeAsync(Tool(handler), Url);

        Assert.False(result.IsError);
        Assert.Equal(
            string.Join(
                '\n',
                $"url          {Url}",
                "status       200 OK",
                "type         text/plain; charset=utf-8",
                "bytes        5",
                string.Empty,
                "hello"),
            result.Content);
        Assert.False(result.IsTruncated);
        Assert.Equal(Url, Assert.Single(result.Effects).Target);
    }

    [Fact]
    public async Task ReducesHtmlToText()
    {
        var handler = new StubHandler().Map(
            Url,
            () => Respond("<html><body><h1>Title</h1><script>track()</script><p>Body &amp; more</p></body></html>", "text/html"));
        var result = await InvokeAsync(Tool(handler), Url);

        Assert.EndsWith("Title\nBody & more", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("track", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", result.Content, StringComparison.Ordinal);
        Assert.Contains("type         text/html; charset=utf-8", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FollowsARedirectAndNamesTheFinalUrl()
    {
        var handler = new StubHandler()
            .MapRedirect(Url, "/final")
            .Map("https://example.com/final", () => Respond("moved here"));

        var result = await InvokeAsync(Tool(handler), Url);

        Assert.False(result.IsError);
        Assert.Contains("url          https://example.com/final", result.Content, StringComparison.Ordinal);
        Assert.Contains($"redirected   from {Url}", result.Content, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://example.com/final", Assert.Single(result.Effects).Target);
    }

    [Fact]
    public async Task GivesUpAtTheRedirectCeiling()
    {
        var handler = new StubHandler()
            .MapRedirect("https://example.com/1", "https://example.com/2")
            .MapRedirect("https://example.com/2", "https://example.com/3")
            .MapRedirect("https://example.com/3", "https://example.com/4")
            .MapRedirect("https://example.com/4", "https://example.com/5");

        var result = await InvokeAsync(
            Tool(handler, new WebFetchOptions { MaxRedirects = 2 }),
            "https://example.com/1");

        Assert.True(result.IsError);
        Assert.Contains("gave up after 2 redirects", result.Content, StringComparison.Ordinal);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task RefusesALoopbackAddressBeforeAnythingIsSent()
    {
        var handler = new StubHandler();
        var result = await InvokeAsync(Tool(handler), "http://localhost:8080/status");

        Assert.True(result.IsRefused);
        Assert.Contains("127.0.0.1", result.Content, StringComparison.Ordinal);
        Assert.Contains("loopback", result.Content, StringComparison.Ordinal);
        Assert.Contains("--web-allow localhost", result.Content, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReadsANamedLoopbackHost()
    {
        var handler = new StubHandler().Map("http://localhost:8080/status", () => Respond("ok"));
        var result = await InvokeAsync(
            Tool(handler, new WebFetchOptions { AllowedHosts = ["localhost:8080"] }),
            "http://localhost:8080/status");

        Assert.False(result.IsError);
        Assert.EndsWith("ok", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesWhenAnyResolvedAddressIsPrivate()
    {
        var handler = new StubHandler();
        var result = await InvokeAsync(
            Tool(handler, resolver: new StubResolver().Maps("example.com", "93.184.216.34", "10.0.0.5")),
            Url);

        Assert.True(result.IsRefused);
        Assert.Contains("private", result.Content, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RefusesARedirectIntoAPrivateAddress()
    {
        var handler = new StubHandler().MapRedirect(Url, "http://127.0.0.1/secret");
        var result = await InvokeAsync(Tool(handler), Url);

        Assert.True(result.IsRefused);
        Assert.Contains("127.0.0.1", result.Content, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RefusesASchemeThatIsNotHttpOrHttps()
    {
        var handler = new StubHandler();
        var result = await InvokeAsync(Tool(handler), "file:///etc/passwd");

        Assert.True(result.IsRefused);
        Assert.Contains("http and https only", result.Content, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RefusesAUrlThatCarriesCredentials()
    {
        var handler = new StubHandler();
        var result = await InvokeAsync(Tool(handler), "https://user:secret@example.com/page");

        Assert.True(result.IsRefused);
        Assert.Contains("user name or password", result.Content, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SendsOneUserAgentAndNoCredentialOrCookie()
    {
        var handler = new StubHandler().Map(Url, () => Respond("hello"));
        await InvokeAsync(Tool(handler), Url);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Contains("User-Agent: sharpcoreagent", request.Headers, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", request.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie", request.Headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Referer", request.Headers, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TruncatesAtTheCharacterCeiling()
    {
        var handler = new StubHandler().Map(Url, () => Respond(new string('a', 1_000)));
        var result = await InvokeAsync(Tool(handler, new WebFetchOptions { MaxChars = 100 }), Url);

        Assert.True(result.IsTruncated);
        Assert.Contains("truncated    yes", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("[truncated: kept the first 100 of 1000 characters]", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopsReadingAtTheByteCeiling()
    {
        var handler = new StubHandler().Map(Url, () => Respond(new string('a', 4_096)));
        var result = await InvokeAsync(Tool(handler, new WebFetchOptions { MaxBytes = 16 }), Url);

        Assert.True(result.IsTruncated);
        Assert.Contains("bytes        16 (read stopped at the 16-byte ceiling)", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnswersAFailedStatusAsAFailureThatCarriesIt()
    {
        var handler = new StubHandler().Map(
            Url,
            () => Respond("no such page", status: HttpStatusCode.NotFound, reason: "Not Found"));
        var result = await InvokeAsync(Tool(handler), Url);

        Assert.True(result.IsError);
        Assert.Contains("status       404 Not Found", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("no such page", result.Content, StringComparison.Ordinal);
        Assert.Empty(result.Effects);
    }

    [Fact]
    public async Task RefusesABinaryContentType()
    {
        var handler = new StubHandler().Map(Url, () => Respond("binary", "image/png"));
        var result = await InvokeAsync(Tool(handler), Url);

        Assert.True(result.IsError);
        Assert.Contains("image/png", result.Content, StringComparison.Ordinal);
        Assert.Contains("does not read", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAHostThatDoesNotResolve()
    {
        var handler = new StubHandler();
        var result = await InvokeAsync(Tool(handler, resolver: new StubResolver { Fails = true }), Url);

        Assert.True(result.IsError);
        Assert.False(result.IsRefused);
        Assert.Contains("does not resolve", result.Content, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void IsReadOnlyAndTakesOnlyAUrl()
    {
        var tool = Tool(new StubHandler());

        Assert.Equal("fetch_url", tool.Definition.Name);
        Assert.Equal(ToolSafety.ReadOnly, tool.Safety);
        Assert.Contains("\"required\":[\"url\"]", tool.Definition.ParametersSchemaJson, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\":false", tool.Definition.ParametersSchemaJson, StringComparison.Ordinal);

        // The description carries the three facts a model cannot infer from the schema: no JavaScript, no credential,
        // and no private address.
        Assert.Contains("no cookie", tool.Definition.Description, StringComparison.Ordinal);
        Assert.Contains("never runs JavaScript", tool.Definition.Description, StringComparison.Ordinal);
        Assert.Contains("Refuses loopback", tool.Definition.Description, StringComparison.Ordinal);
    }

    private static FetchUrlTool Tool(
        StubHandler handler,
        WebFetchOptions? options = null,
        IWebAddressResolver? resolver = null) =>
        new(new StubClientFactory(handler), options ?? WebFetchOptions.Default, resolver ?? new StubResolver());

    private static async Task<ToolResult> InvokeAsync(FetchUrlTool tool, string url)
    {
        using var sandbox = ToolTestWorkspace.Create();
        return await tool.ExecuteAsync(
            ToolTestWorkspace.Call("fetch_url", ToolTestWorkspace.Args(new { url })),
            sandbox.Context,
            CancellationToken.None);
    }

    private static HttpResponseMessage Respond(
        string body,
        string mediaType = "text/html",
        HttpStatusCode status = HttpStatusCode.OK,
        string reason = "OK") => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType),
            ReasonPhrase = reason,
        };

    /// <summary>One request as it was actually sent, copied before the tool disposes it.</summary>
    private sealed record RequestRecord(string Method, string RequestUrl, string Headers);

    /// <summary>
    /// Answers from a table of URLs and records what was asked.
    /// </summary>
    /// <remarks>
    /// A delegating handler rather than a fake client: it is the seam the provider adapters are already tested through,
    /// and it never redirects on its own, so a redirect test proves the tool's own loop rather than the platform's.
    /// </remarks>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

        public List<RequestRecord> Requests { get; } = [];

        public StubHandler Map(string url, Func<HttpResponseMessage> response)
        {
            _routes[url] = response;
            return this;
        }

        public StubHandler MapRedirect(string url, string location)
        {
            _routes[url] = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found) { ReasonPhrase = "Found" };
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
                return response;
            };
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestRecord(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.ToString()));

            return _routes.TryGetValue(request.RequestUri.AbsoluteUri, out var response)
                ? Task.FromResult(response())
                : throw new HttpRequestException($"the test has no response for {request.RequestUri}");
        }
    }

    private sealed class StubClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) =>
            new(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class StubResolver : IWebAddressResolver
    {
        private const string DefaultPublicAddress = "93.184.216.34";

        private readonly Dictionary<string, IPAddress[]> _map = new(StringComparer.OrdinalIgnoreCase)
        {
            // The one name every platform resolves locally: a test that used the public default for it would prove
            // nothing about the loopback refusal.
            ["localhost"] = [IPAddress.Loopback],
        };

        public bool Fails { get; init; }

        public StubResolver Maps(string host, params string[] addresses)
        {
            _map[host] = [.. addresses.Select(IPAddress.Parse)];
            return this;
        }

        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            if (Fails)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }

            if (_map.TryGetValue(host, out var mapped))
            {
                return ValueTask.FromResult<IReadOnlyList<IPAddress>>(mapped);
            }

            return ValueTask.FromResult<IReadOnlyList<IPAddress>>(
                IPAddress.TryParse(host, out var literal) ? [literal] : [IPAddress.Parse(DefaultPublicAddress)]);
        }
    }
}
