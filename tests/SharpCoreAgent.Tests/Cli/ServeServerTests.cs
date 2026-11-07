using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using SharpCoreAgent.Cli;
using SharpCoreAgent.Domain.Agents;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The web server on a real loopback port: the page, the state, a turn, and the refusals with nothing in them.
/// </summary>
/// <remarks>
/// Skips rather than fails where the platform refuses the prefix, the way <see cref="UiServerTests"/> does: a sandbox that
/// will not let <c>http.sys</c> register <c>http://127.0.0.1:&lt;port&gt;/</c> is a fact about the machine. The port is
/// chosen with <c>--port 0</c>, so two runs of the suite never collide. The turn is a stub, so the loop is not involved:
/// what is asserted is the transport — a prompt is accepted, the state is the state, and an event reaches the stream.
/// </remarks>
public sealed class ServeServerTests
{
    [Fact]
    public async Task TheRoutesAnswerAndRefusalsCarryNothing()
    {
        var hub = new ServeHub();
        var approver = new WebApprover(hub);
        var errors = new StringWriter(CultureInfo.InvariantCulture);
        var ran = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = ApprovalMode.Ask;
        var mode = AgentMode.Act;

        if (!ServeServer.TryStart(
                0,
                hub,
                approver,
                (prompt, _) => { ran.TrySetResult(prompt); return Task.CompletedTask; },
                () => new ServeState("demo:demo", approval.ToString(), mode.ToString(), 0, long.MaxValue),
                applied => approval = applied,
                applied => mode = applied,
                errors,
                out var server))
        {
            Assert.Skip($"the platform refused a loopback prefix: {errors}");
            return;
        }

        using (server)
        using (var cancellation = new CancellationTokenSource())
        {
            var serving = server!.ServeAsync(cancellation.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var root = new Uri(server.Url);
            var token = TestContext.Current.CancellationToken;

            var page = await client.GetAsync(root, token);
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("<!DOCTYPE html>", await page.Content.ReadAsStringAsync(token), StringComparison.Ordinal);
            Assert.Equal("no-store", string.Join(",", page.Headers.GetValues("Cache-Control")));

            var state = await client.GetAsync(new Uri(root, "/api/state"), token);
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
            using (var document = JsonDocument.Parse(await state.Content.ReadAsStringAsync(token)))
            {
                Assert.Equal("demo:demo", document.RootElement.GetProperty("model").GetString());
            }

            var empty = await client.PostAsync(
                new Uri(root, "/api/turn"),
                new StringContent("{}", Encoding.UTF8, "application/json"),
                token);
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

            var badSetting = await client.PostAsync(
                new Uri(root, "/api/settings"),
                new StringContent("{\"approval\":\"nope\"}", Encoding.UTF8, "application/json"),
                token);
            Assert.Equal(HttpStatusCode.BadRequest, badSetting.StatusCode);

            using var rebound = new HttpRequestMessage(HttpMethod.Get, root);
            rebound.Headers.Host = "evil.example";
            var foreign = await client.SendAsync(rebound, token);
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            Assert.Empty(await foreign.Content.ReadAsStringAsync(token));

            var unknown = await client.GetAsync(new Uri(root, "/api/other"), token);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Empty(await unknown.Content.ReadAsStringAsync(token));

            var turn = await client.PostAsync(
                new Uri(root, "/api/turn"),
                new StringContent("{\"prompt\":\"hello\"}", Encoding.UTF8, "application/json"),
                token);
            Assert.Equal(HttpStatusCode.Accepted, turn.StatusCode);
            Assert.Equal("hello", await ran.Task.WaitAsync(TimeSpan.FromSeconds(10), token));

            await cancellation.CancelAsync();
            await serving;
        }
    }

    [Fact]
    public async Task AnEventReachesTheStreamingClient()
    {
        var hub = new ServeHub();
        var approver = new WebApprover(hub);
        var errors = new StringWriter(CultureInfo.InvariantCulture);

        if (!ServeServer.TryStart(
                0,
                hub,
                approver,
                (_, _) => Task.CompletedTask,
                () => new ServeState("demo:demo", "ask", "act", 0, long.MaxValue),
                _ => { },
                _ => { },
                errors,
                out var server))
        {
            Assert.Skip($"the platform refused a loopback prefix: {errors}");
            return;
        }

        using (server)
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            var serving = server!.ServeAsync(cancellation.Token);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var root = new Uri(server.Url);

            using var response = await client.GetAsync(
                new Uri(root, "/api/events"),
                HttpCompletionOption.ResponseHeadersRead,
                cancellation.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellation.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var opening = await reader.ReadLineAsync(cancellation.Token);
            Assert.NotNull(opening);
            Assert.StartsWith(":", opening, StringComparison.Ordinal);

            hub.Publish(ServeEvents.Text("hi"));

            string? line;
            do
            {
                line = await reader.ReadLineAsync(cancellation.Token);
            }
            while (line is not null && !line.StartsWith("data: ", StringComparison.Ordinal));

            Assert.NotNull(line);
            Assert.Contains("\"text_delta\"", line, StringComparison.Ordinal);
            Assert.Contains("\"hi\"", line, StringComparison.Ordinal);

            await cancellation.CancelAsync();
            await serving;
        }
    }
}
