using System.Net;
using System.Net.Sockets;
using System.Text;
using SharpCoreAgent.Infrastructure.Web;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The gap this closes: the tool judges a hop from the addresses a name resolves to, and a name can answer with one
/// address for that judgement and another for the socket. These tests assert the second judgement, at the point where
/// a socket is opened, and the one setting that would otherwise hand the decision to someone else.
/// </summary>
public sealed class WebFetchHandlerTests
{
    [Fact]
    public void TheFetchHandlerRefusesAProxy()
    {
        // Through a proxy the callback is handed the proxy's endpoint and the proxy resolves the name itself, so the
        // address refusal would be advice rather than a rule.
        using var handler = WebFetchHandler.Create(new StubResolver(IPAddress.Loopback), new WebFetchOptions());

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task ANameThatResolvesToLoopbackAtConnectTimeIsRefused()
    {
        // The resolver already answered "public" when the hop was checked; the socket is being opened after it flipped.
        using var handler = WebFetchHandler.Create(
            new StubResolver(IPAddress.Loopback),
            new WebFetchOptions());
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://rebind.test/");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, TestContext.Current.CancellationToken));

        Assert.Contains("refused at connect time", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("loopback", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("--web-allow rebind.test", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostTheDeveloperNamedIsConnectedAndRead()
    {
        using var server = new LoopbackServer();
        using var handler = WebFetchHandler.Create(
            new StubResolver(IPAddress.Loopback),
            new WebFetchOptions { AllowedHosts = [$"127.0.0.1:{server.Port}"] });
        using var client = new HttpClient(handler);

        var body = await client.GetStringAsync(new Uri($"http://127.0.0.1:{server.Port}/"), TestContext.Current.CancellationToken);

        Assert.Contains("hello from a socket", body, StringComparison.Ordinal);
        Assert.Contains("GET / HTTP/1.1", server.RequestLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamingOnePortDoesNotOpenAnother()
    {
        using var server = new LoopbackServer();
        using var handler = WebFetchHandler.Create(
            new StubResolver(IPAddress.Loopback),
            new WebFetchOptions { AllowedHosts = [$"127.0.0.1:{server.Port + 1}"] });
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync(new Uri($"http://127.0.0.1:{server.Port}/"), TestContext.Current.CancellationToken));

        Assert.Contains("refused at connect time", exception.ToString(), StringComparison.Ordinal);
    }

    private sealed class StubResolver(IPAddress answer) : IWebAddressResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<IPAddress>>([answer]);
    }

    /// <summary>
    /// One request, one response, on a port the operating system picked — the only socket the suite opens.
    /// </summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _served;

        public LoopbackServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _served = ServeAsync();
        }

        public int Port { get; }

        public string RequestLine { get; private set; } = string.Empty;

        public void Dispose()
        {
            _listener.Stop();
            try
            {
                _served.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The listener was stopped under the accept: the test already has what it asked for.
            }
        }

        private async Task ServeAsync()
        {
            using var socket = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            RequestLine = await reader.ReadLineAsync().ConfigureAwait(false) ?? string.Empty;
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync().ConfigureAwait(false)))
            {
                // The headers are not what this test is about; the blank line ends them.
            }

            const string Body = "hello from a socket";
            var response = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"HTTP/1.1 200 OK\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {Body.Length}\r\nConnection: close\r\n\r\n{Body}");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response)).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
    }
}
