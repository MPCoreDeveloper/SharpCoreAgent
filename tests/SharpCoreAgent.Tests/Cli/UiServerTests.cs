using System.Globalization;
using System.Net;
using SharpCoreAgent.Cli;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The listener on a real loopback port: the two routes, the refusals, and the stop that leaves no port behind.
/// </summary>
/// <remarks>
/// The one test in this round that opens a socket, and it skips rather than fails where the platform refuses the prefix:
/// a sandbox that will not let <c>http.sys</c> register <c>http://127.0.0.1:&lt;port&gt;/</c> is a fact about the machine,
/// not a defect in the listener. The port is chosen with <c>--port 0</c>, so two runs of the suite never collide.
/// </remarks>
public sealed class UiServerTests
{
    [Fact]
    public async Task TheTwoRoutesAnswerAndEverythingElseIsRefusedWithNothingInIt()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Circle.cs", "namespace Demo;\n\ninternal sealed class Circle { }\n");
        var workspace = WorkspaceFor(sandbox);
        await using var index = SharpCoreAgentIndex.Open(workspace);
        await index.EnsureFreshAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var errors = new StringWriter(CultureInfo.InvariantCulture);
        if (!UiServer.TryStart(0, index, workspace, false, TimeSpan.FromSeconds(3), errors, out var server))
        {
            Assert.Skip($"the platform refused a loopback prefix: {errors}");
            return;
        }

        using (server)
        using (var cancellation = new CancellationTokenSource())
        {
            var serving = server!.ServeAsync(cancellation.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var root = new Uri(server.Endpoint.Url);
            var token = TestContext.Current.CancellationToken;

            var status = await index.GetStatusAsync(token);
            var expected = IndexStatusDocument.Serialize(IndexStatusDocument.Build(workspace, status));

            var page = await client.GetAsync(root, token);
            var pageBody = await page.Content.ReadAsStringAsync(token);

            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Equal("text/html; charset=utf-8", page.Content.Headers.ContentType?.ToString());
            Assert.Equal("no-store", string.Join(",", page.Headers.GetValues("Cache-Control")));
            Assert.Contains("<!DOCTYPE html>", pageBody, StringComparison.Ordinal);
            Assert.Contains("data-field=\"files\"", pageBody, StringComparison.Ordinal);

            var statusResponse = await client.GetAsync(new Uri(root, "/api/status.json"), token);
            var statusBody = await statusResponse.Content.ReadAsStringAsync(token);

            Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
            Assert.Equal("application/json; charset=utf-8", statusResponse.Content.Headers.ContentType?.ToString());
            Assert.Equal("no-store", string.Join(",", statusResponse.Headers.GetValues("Cache-Control")));
            Assert.Equal(expected, statusBody);

            var refused = await client.PostAsync(root, new StringContent("ignored"), token);
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.Empty(await refused.Content.ReadAsStringAsync(token));

            using var rebound = new HttpRequestMessage(HttpMethod.Get, root);
            rebound.Headers.Host = "evil.example";
            var foreign = await client.SendAsync(rebound, token);
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            Assert.Empty(await foreign.Content.ReadAsStringAsync(token));

            var elsewhere = await client.GetAsync(new Uri(root, "/api/other"), token);
            Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
            Assert.Empty(await elsewhere.Content.ReadAsStringAsync(token));

            await cancellation.CancelAsync();
            await serving;
            Assert.Empty(errors.ToString());
        }
    }

    [Fact]
    public async Task AnExplicitPortThatIsAlreadyHeldIsReportedRatherThanThrown()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var workspace = WorkspaceFor(sandbox);
        await using var index = SharpCoreAgentIndex.Open(workspace);
        var quiet = new StringWriter(CultureInfo.InvariantCulture);

        if (!UiServer.TryStart(0, index, workspace, false, TimeSpan.FromSeconds(3), quiet, out var first))
        {
            Assert.Skip($"the platform refused a loopback prefix: {quiet}");
            return;
        }

        using (first)
        {
            var errors = new StringWriter(CultureInfo.InvariantCulture);

            var second = UiServer.TryStart(
                first!.Endpoint.Port,
                index,
                workspace,
                false,
                TimeSpan.FromSeconds(3),
                errors,
                out var held);

            Assert.False(second);
            Assert.Null(held);
            Assert.Contains($"http://127.0.0.1:{first.Endpoint.Port}/", errors.ToString(), StringComparison.Ordinal);
            Assert.Contains("--port", errors.ToString(), StringComparison.Ordinal);
        }
    }

    private static Workspace WorkspaceFor(ToolTestWorkspace sandbox) =>
        CliOptions.Parse(["--workspace", sandbox.Root], _ => null).ToSharpCoreAgentOptions().CreateWorkspace();
}
