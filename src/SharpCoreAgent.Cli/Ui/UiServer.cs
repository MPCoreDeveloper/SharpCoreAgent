using System.Globalization;
using System.Net;
using System.Text;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Indexing;

namespace SharpCoreAgent.Cli;

/// <summary>
/// The panel's listener: two routes on a loopback prefix, one request at a time, and nothing else.
/// </summary>
/// <remarks>
/// <c>HttpListener</c> rather than a hand-written socket loop and rather than Kestrel (plan D5): it is framework, it
/// parses HTTP correctly, and on Windows it is a component the operating system already runs. What it is not is private —
/// any process on the machine can reach a loopback port, and a page in any browser can reach it by DNS rebinding — so
/// every request is checked against the bound authority before anything is rendered, and the refusal carries an empty
/// body with no number in it.
/// <para>
/// One request at a time is deliberate and cheap: the answer is a status read and a string, there is one developer, and a
/// concurrent accept loop would be the only part of this class with a race in it. The index handle is opened by the
/// command and shared, which is why the server takes one rather than opening its own.
/// </para>
/// </remarks>
internal sealed class UiServer : IDisposable
{
    /// <summary>The content type of the page.</summary>
    private const string Html = "text/html; charset=utf-8";

    /// <summary>The content type of the status object.</summary>
    private const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>
    /// How many ports <c>--port 0</c> tries before giving up.
    /// </summary>
    /// <remarks>
    /// The port is chosen with a socket and then registered with <c>http.sys</c>, and another process can take it in
    /// between. Five attempts makes that losing race a curiosity rather than a command that fails for a reason nobody can
    /// see; an explicit port is never retried, because a number a developer named that is busy is a fact to report.
    /// </remarks>
    private const int EphemeralAttempts = 5;

    private readonly HttpListener _listener;
    private readonly SharpCoreAgentIndex _index;
    private readonly Workspace _workspace;
    private readonly bool _refresh;
    private readonly TimeSpan _budget;
    private readonly TextWriter _error;
    private bool _disposed;

    private UiServer(
        HttpListener listener,
        UiEndpoint endpoint,
        SharpCoreAgentIndex index,
        Workspace workspace,
        bool refresh,
        TimeSpan budget,
        TextWriter error)
    {
        _listener = listener;
        Endpoint = endpoint;
        _index = index;
        _workspace = workspace;
        _refresh = refresh;
        _budget = budget;
        _error = error;
    }

    /// <summary>Gets the address the listener was bound to.</summary>
    internal UiEndpoint Endpoint { get; }

    /// <summary>
    /// Binds the listener, choosing a port when none was named.
    /// </summary>
    /// <param name="requestedPort">The port from <c>--port</c>, or 0 for one chosen now.</param>
    /// <param name="index">The open index both routes read.</param>
    /// <param name="workspace">The workspace the index describes.</param>
    /// <param name="refresh">Whether the index is walked before each answer.</param>
    /// <param name="budget">How long that walk may take.</param>
    /// <param name="error">Where the reason goes when the port cannot be bound.</param>
    /// <param name="server">The bound server, or <see langword="null"/> when the port was refused.</param>
    /// <returns><see langword="true"/> when something is listening.</returns>
    /// <exception cref="CliUsageException">No loopback port could be chosen at all.</exception>
    internal static bool TryStart(
        int requestedPort,
        SharpCoreAgentIndex index,
        Workspace workspace,
        bool refresh,
        TimeSpan budget,
        TextWriter error,
        out UiServer? server)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(error);

        server = null;
        var attempts = requestedPort == 0 ? EphemeralAttempts : 1;
        HttpListenerException? refusal = null;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var endpoint = UiEndpoint.Loopback(requestedPort == 0 ? UiEndpoint.ChoosePort() : requestedPort);
            var listener = new HttpListener();
            listener.Prefixes.Add(endpoint.Url);

            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                refusal = ex;
                continue;
            }

            server = new UiServer(listener, endpoint, index, workspace, refresh, budget, error);
            return true;
        }

        error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"error: could not listen on http://{UiEndpoint.LoopbackHost}:{requestedPort}/: {refusal?.Message} (code {refusal?.ErrorCode})"));
        error.WriteLine("hint: name another port with --port, or use --port 0 for one the system picks.");

        return false;
    }

    /// <summary>
    /// Answers requests until the process is interrupted.
    /// </summary>
    /// <param name="cancellationToken">Cancelled by Ctrl+C, which ends the loop rather than the process.</param>
    /// <returns>A task that completes when the listener has stopped.</returns>
    /// <remarks>
    /// The listener is stopped and closed in the <see langword="finally"/> so an interrupted panel leaves no half-open
    /// port behind it: the next <c>ui --serve</c> on the same explicit port must work, and it would not if the
    /// registration outlived the process's own cleanup. A request that fails is answered with the failure and the loop
    /// continues — one unreadable index is not a reason to stop listening.
    /// </remarks>
    internal async Task ServeAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    // WaitAsync rather than a raw await: Ctrl+C has to end the wait, and the pending GetContext is
                    // abandoned when the listener is closed below. Nothing observes its failure, which is why the
                    // disposal is in the finally rather than left to the caller.
                    context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    // The listener was disposed underneath the wait: the process is stopping.
                    break;
                }

                await AnswerAsync(context, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>
    /// Answers one request: the page, the status object, or a refusal with nothing in it.
    /// </summary>
    /// <param name="context">The request and its response.</param>
    /// <param name="cancellationToken">Cancels the index read.</param>
    /// <returns>A task that completes when the response is closed.</returns>
    private async Task AnswerAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var response = context.Response;
        try
        {
            // no-store on every answer, the refusals included: a cached page would show numbers that are no longer the
            // index's, which is the failure this command exists to end.
            response.Headers["Cache-Control"] = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.KeepAlive = false;

            if (!string.Equals(context.Request.Headers["Host"], Endpoint.Authority, StringComparison.Ordinal))
            {
                // FR-004: a loopback listener that answers any Host is reachable from a hostile page by DNS rebinding.
                // Checked before the path, so a rebound request learns nothing about which routes exist.
                Refuse(response, 403);
                return;
            }

            if (!string.Equals(context.Request.HttpMethod, "GET", StringComparison.Ordinal))
            {
                Refuse(response, 404);
                return;
            }

            var path = context.Request.Url?.AbsolutePath;
            if (string.Equals(path, "/", StringComparison.Ordinal))
            {
                await WritePageAsync(response, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/api/status.json", StringComparison.Ordinal))
            {
                await WriteStatusAsync(response, cancellationToken).ConfigureAwait(false);
                return;
            }

            Refuse(response, 404);
        }
#pragma warning disable CA1031 // A page that fails to render must not end the panel: the failure is answered and the loop continues.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _error.WriteLineAsync($"error: could not answer a request: {ex.Message}".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            TryRefuse(response, 500);
        }
        finally
        {
            response.Close();
        }
    }

    /// <summary>
    /// Writes the page, or the failure in its place.
    /// </summary>
    /// <param name="response">The response to fill.</param>
    /// <param name="cancellationToken">Cancels the index read.</param>
    /// <returns>A task that completes when the response is filled.</returns>
    private async Task WritePageAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        var (document, failure) = await ReadAsync(cancellationToken).ConfigureAwait(false);

        if (document is null)
        {
            Write(response, 503, Html, FailurePage(failure));
            return;
        }

        Write(response, 200, Html, UiPage.Render(document));
    }

    /// <summary>
    /// Writes the status object, which is the same object <c>index --status --json</c> prints.
    /// </summary>
    /// <param name="response">The response to fill.</param>
    /// <param name="cancellationToken">Cancels the index read.</param>
    /// <returns>A task that completes when the response is filled.</returns>
    private async Task WriteStatusAsync(HttpListenerResponse response, CancellationToken cancellationToken)
    {
        var (document, failure) = await ReadAsync(cancellationToken).ConfigureAwait(false);

        if (document is null)
        {
            // One key rather than a document of zeroes: a script that asked for the status is told the read failed, and
            // nothing in this body can be mistaken for a number.
            Write(response, 503, JsonContentType, IndexStatusDocument.Serialize(new Dictionary<string, object?>
            {
                ["error"] = failure,
            }));
            return;
        }

        Write(response, 200, JsonContentType, IndexStatusDocument.Serialize(document));
    }

    /// <summary>
    /// Reads the status, refreshing first when <c>--refresh</c> asked for it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The document, or the reason there is none.</returns>
    /// <remarks>
    /// The refresh is bounded by <c>--index-budget</c> with the same two-argument call <c>run</c> and <c>chat</c> make, so
    /// the panel and a turn mean one thing by that number. A failed read is returned rather than thrown: the page says
    /// so and the server keeps serving, because a locked index is a condition the next click may find gone.
    /// </remarks>
    private async Task<(Dictionary<string, object?>? Document, string Failure)> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_refresh)
            {
                await _index.EnsureFreshAsync(_budget, cancellationToken).ConfigureAwait(false);
            }

            var status = await _index.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return (IndexStatusDocument.Build(_workspace, status), string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or SharpCoreAgentException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Writes a body with its status, type and length.</summary>
    /// <param name="response">The response to fill.</param>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="contentType">The content type, including the charset.</param>
    /// <param name="body">The text, encoded as UTF-8.</param>
    private static void Write(HttpListenerResponse response, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
    }

    /// <summary>
    /// Refuses a request with a status and no body at all.
    /// </summary>
    /// <param name="response">The response to fill.</param>
    /// <param name="status">403 for a foreign host, 404 for anything else.</param>
    /// <remarks>
    /// An empty body is the point: FR-004 says the refusal carries no number, so a rebound page cannot learn how many
    /// files are indexed from the shape of a rejection.
    /// </remarks>
    private static void Refuse(HttpListenerResponse response, int status)
    {
        response.StatusCode = status;
        response.ContentLength64 = 0;
    }

    /// <summary>Refuses a request whose socket may already be gone.</summary>
    /// <param name="response">The response to fill.</param>
    /// <param name="status">The status to answer with, when anything can still be written.</param>
    private static void TryRefuse(HttpListenerResponse response, int status)
    {
        try
        {
            Refuse(response, status);
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The client hung up before the failure could be described to it. Nothing to report and nothing to do.
        }
    }

    /// <summary>
    /// The page shown when the index cannot be read.
    /// </summary>
    /// <param name="failure">The message the index reader raised.</param>
    /// <returns>A minimal page: the failure, escaped, and nothing else.</returns>
    private static string FailurePage(string failure) => $"""
          <!DOCTYPE html>
          <html lang="en">
          <head>
          <meta charset="utf-8">
          <meta name="color-scheme" content="light dark">
          <title>sharpcoreagent · index</title>
          <link rel="icon" href="data:,">
          </head>
          <body style="font: 15px/1.5 system-ui, sans-serif; margin: 2.5rem">
          <h1 style="font-size: 1.4rem">the index could not be read</h1>
          <p><code>{WebUtility.HtmlEncode(failure)}</code></p>
          </body>
          </html>

        """;

    /// <summary>Stops and closes the listener, once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _listener.Stop();
        _listener.Close();
    }
}
