using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// One running MCP server, and the JSON-RPC conversation with it.
/// </summary>
/// <remarks>
/// The connection owns three things a server has: a process, its stdin and stdout as one
/// <see cref="JsonRpcPipe"/>, and its stderr as a bounded ring that only ever reaches the verbose log.
/// Nothing here is exposed to the model: the adapter per tool is <see cref="McpTool"/>, and this class is
/// the thing that can fail.
/// <para>
/// Startup is atomic on purpose. <see cref="StartAsync"/> either finishes the handshake and reads a
/// non-empty tool list, or throws <see cref="McpStartupException"/> naming the command line and the
/// reason; there is no half-started server whose missing tools a later turn has to explain.
/// </para>
/// <para>
/// The stream constructor exists so the protocol can be tested without a child process, which is what
/// keeps the suite deterministic; the command-line constructor is the one the product uses.
/// </para>
/// </remarks>
public sealed class McpServerConnection : IAsyncDisposable
{
    private readonly McpClientOptions _options;
    private readonly Action<string>? _log;
    private readonly List<McpToolInfo> _tools = [];
    private readonly Queue<string> _stderr = new();
    private readonly object _stderrGate = new();
    private readonly EventHandler _processExit;
    private readonly Stream? _input;
    private readonly Stream? _output;
    private Process? _process;
    private JsonRpcPipe? _pipe;
    private Task? _stderrPump;
    private CancellationTokenSource? _stderrStop;
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Creates a connection that starts one server command line when it is started.
    /// </summary>
    /// <param name="name">The prefix this server's tools carry.</param>
    /// <param name="commandLine">The command line exactly as the developer typed it.</param>
    /// <param name="options">The ceilings the conversation works within.</param>
    /// <param name="log">Receives stderr lines while <c>--verbose</c> is in force.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="commandLine"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.</exception>
    public McpServerConnection(string name, string commandLine, McpClientOptions options, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        Name = name;
        CommandLine = commandLine.Trim();
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
        _processExit = (_, _) => KillQuietly();

        // Until the server answers, the version this client sends is the one it can honestly report.
        ProtocolVersion = _options.ProtocolVersion;
    }

    /// <summary>
    /// Creates a connection over an already-open pair of streams, for tests and for embedders that own
    /// the process themselves.
    /// </summary>
    /// <param name="name">The prefix this server's tools carry.</param>
    /// <param name="commandLine">What to call the server in messages.</param>
    /// <param name="input">Where the server writes.</param>
    /// <param name="output">Where the server reads.</param>
    /// <param name="options">The ceilings the conversation works within.</param>
    /// <param name="log">Receives the pipe's diagnostics.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    internal McpServerConnection(
        string name,
        string commandLine,
        Stream input,
        Stream output,
        McpClientOptions options,
        Action<string>? log = null)
        : this(name, commandLine, options, log)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>Gets the prefix this server's tools carry, such as <c>npx</c> or <c>npx_2</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the command line the developer typed.</summary>
    public string CommandLine { get; }

    /// <summary>Gets the protocol version the server answered with, or the one this client sends.</summary>
    public string ProtocolVersion { get; private set; }

    /// <summary>Gets the tools the server offered at startup, in the order it offered them.</summary>
    public IReadOnlyList<McpToolInfo> Tools => _tools;

    /// <summary>Gets the last lines the server wrote to stderr, newest last.</summary>
    internal IReadOnlyList<string> Stderr
    {
        get
        {
            lock (_stderrGate)
            {
                return [.. _stderr];
            }
        }
    }

    /// <summary>
    /// Starts the server if it is not running, then performs the handshake and reads its tools.
    /// </summary>
    /// <param name="cancellationToken">Cancels startup; the server is then left for disposal to kill.</param>
    /// <returns>The start, once the tool list is known.</returns>
    /// <exception cref="McpStartupException">
    /// The process would not start, the handshake failed, the server closed early, or it offered no tools.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_started)
        {
            return;
        }

        try
        {
            EnsurePipe();
            await HandshakeAsync(cancellationToken).ConfigureAwait(false);
            await LoadToolsAsync(cancellationToken).ConfigureAwait(false);

            if (_tools.Count == 0)
            {
                throw new McpStartupException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{Name}' ({CommandLine}) started but offered no tools"));
            }

            _started = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (McpStartupException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Every other failure reads the same to a developer — one line naming the command and the
            // reason — which is why the wrapping happens here rather than at each call site.
            throw new McpStartupException(
                string.Create(CultureInfo.InvariantCulture, $"'{Name}' ({CommandLine}): {ex.Message}{ExitNote()}"),
                ex);
        }
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        var parameters = new JsonObject
        {
            ["protocolVersion"] = _options.ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject
            {
                ["name"] = _options.ClientName,
                ["version"] = ClientVersion,
            },
        };

        var result = await _pipe!
            .RequestAsync("initialize", parameters, _options.ControlTimeout, cancellationToken)
            .ConfigureAwait(false);

        // The server's version is accepted whatever it is: the versions in the wild differ in methods this
        // client never calls, and refusing a server over a date would cost the developer their tools.
        if (result.ValueKind == JsonValueKind.Object &&
            result.TryGetProperty("protocolVersion", out var version) &&
            version.ValueKind == JsonValueKind.String &&
            version.GetString() is { Length: > 0 } answered)
        {
            ProtocolVersion = answered;
        }

        await _pipe.NotifyAsync("notifications/initialized", null, cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadToolsAsync(CancellationToken cancellationToken)
    {
        string? cursor = null;

        for (var page = 1; page <= _options.MaxToolPages; page++)
        {
            var parameters = new JsonObject();
            if (cursor is not null)
            {
                parameters["cursor"] = cursor;
            }

            var result = await _pipe!
                .RequestAsync("tools/list", parameters, _options.ControlTimeout, cancellationToken)
                .ConfigureAwait(false);

            if (result.ValueKind != JsonValueKind.Object)
            {
                throw new McpStartupException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{Name}' ({CommandLine}) answered tools/list without an object"));
            }

            if (result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in tools.EnumerateArray())
                {
                    if (McpToolInfo.TryRead(entry, out var info))
                    {
                        _tools.Add(info);
                    }
                    else
                    {
                        _log?.Invoke(string.Create(
                            CultureInfo.InvariantCulture,
                            $"{Name}: ignored a tools/list entry with no name"));
                    }
                }
            }

            cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(cursor))
            {
                return;
            }
        }

        // A server that keeps offering another page must not hold startup open, and it has already offered
        // more tools than one turn can usefully choose from: the pages read so far are kept, and the stop is
        // reported rather than hidden.
        _log?.Invoke(string.Create(
            CultureInfo.InvariantCulture,
            $"{Name}: stopped after {_options.MaxToolPages} pages of tools"));
    }

    /// <summary>
    /// Calls one of the server's tools.
    /// </summary>
    /// <param name="tool">The tool name the server knows, without the client's prefix.</param>
    /// <param name="arguments">The argument object, forwarded unchanged.</param>
    /// <param name="timeout">The wall-clock ceiling for this call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The server's answer, or a failure phrased for the model.</returns>
    /// <exception cref="InvalidOperationException">The connection was never started.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public Task<McpCallResult> CallAsync(
        string tool,
        JsonElement arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        CallAsync(tool, arguments, timeout, save: null, cancellationToken);

    /// <summary>
    /// Calls one of the server's tools, offering its binary content to a sink.
    /// </summary>
    /// <param name="tool">The tool name the server knows, without the client's prefix.</param>
    /// <param name="arguments">The argument object, forwarded unchanged.</param>
    /// <param name="timeout">The wall-clock ceiling for this call.</param>
    /// <param name="save">Turns a binary entry of the answer into the sentence describing what became of it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The server's answer, or a failure phrased for the model.</returns>
    /// <remarks>
    /// The overload exists so the connection stays the only place the protocol is spoken while the sink — which
    /// needs a workspace, an approval mode and a tool name — is built by <see cref="McpTool"/>. The public
    /// signature is deliberately unchanged: a connection on its own has no workspace to write into.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The connection was never started.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    internal async Task<McpCallResult> CallAsync(
        string tool,
        JsonElement arguments,
        TimeSpan timeout,
        Func<McpArtefact, string>? save,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        var pipe = _pipe ?? throw new InvalidOperationException("the connection has not been started");

        var parameters = new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = JsonNode.Parse(arguments.GetRawText()),
        };

        try
        {
            var result = await pipe
                .RequestAsync("tools/call", parameters, timeout, cancellationToken)
                .ConfigureAwait(false);

            var isError = result.ValueKind == JsonValueKind.Object &&
                          result.TryGetProperty("isError", out var flag) &&
                          flag.ValueKind == JsonValueKind.True;

            // The answer is already in hand, so a cancellation that arrives now would land mid-save: the
            // remaining artefacts are not written and the caller, who cancelled, sees the cancellation.
            cancellationToken.ThrowIfCancellationRequested();

            return new McpCallResult(isError, McpContent.Flatten(result, Name, save));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (McpTransportException ex)
        {
            // A dead or silent server costs one call, not the session: the developer keeps their transcript
            // and the model learns why the tool is unavailable.
            return McpCallResult.Failure(string.Create(
                CultureInfo.InvariantCulture,
                $"the {Name} server stopped answering: {ex.Message}{ExitNote()}"));
        }
        catch (JsonRpcException ex)
        {
            return McpCallResult.Failure(string.Create(
                CultureInfo.InvariantCulture,
                $"the {Name} server answered {ex.Code}: {ex.Message}"));
        }
    }

    private void EnsurePipe()
    {
        if (_pipe is not null)
        {
            return;
        }

        if (_input is not null && _output is not null)
        {
            _pipe = new JsonRpcPipe(_input, _output, _log);
            return;
        }

        StartProcess();
        _pipe = new JsonRpcPipe(_process!.StandardOutput.BaseStream, _process.StandardInput.BaseStream, _log);
    }

    private void StartProcess()
    {
        var (fileName, arguments, rawArguments) = McpLauncher.For(CommandLine);
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (rawArguments is not null)
        {
            // Windows: the line is handed to cmd exactly as the developer typed it, quotes included.
            startInfo.Arguments = rawArguments;
        }
        else
        {
            // Unix: one argv element, so the kernel passes the developer's quoting through untouched.
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new McpStartupException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{Name}' ({CommandLine}) could not be started"));
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new McpStartupException(
                string.Create(CultureInfo.InvariantCulture, $"'{Name}' ({CommandLine}) could not be started: {ex.Message}"),
                ex);
        }

        _process = process;

        // The backstop the shell tools already use: closing a terminal must not leave a server holding a
        // port or a lock on the next morning's build.
        AppDomain.CurrentDomain.ProcessExit += _processExit;

        _stderrStop = new CancellationTokenSource();
        var stop = _stderrStop;
        _stderrPump = Task.Run(() => DrainStderrAsync(process, stop.Token), stop.Token);
    }

    private async Task DrainStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                lock (_stderrGate)
                {
                    _stderr.Enqueue(line);
                    while (_stderr.Count > _options.MaxStderrLines)
                    {
                        _stderr.Dequeue();
                    }
                }

                // stderr is where a Node server writes its own crash, so a developer chasing one gets the
                // lines as they happen — on the log only, never in the transcript and never merged with stdout.
                _log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"{Name}: {line}"));
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal stopped the drain; the ring already holds what arrived.
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The stream went away with the process.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_pipe is not null)
        {
            // Closing the pipe first ends the conversation politely: the server sees end of input and can
            // exit on its own terms before the kill below.
            await _pipe.DisposeAsync().ConfigureAwait(false);
        }

        if (_stderrStop is { } stop)
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }

        AppDomain.CurrentDomain.ProcessExit -= _processExit;

        if (_process is { } process)
        {
            Kill(process);
            process.Dispose();
            _process = null;
        }

        if (_stderrPump is not null)
        {
            try
            {
                await _stderrPump.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // A server that never closes stderr must not hold shutdown open.
            }
        }

        _stderrStop?.Dispose();
        _stderrStop = null;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // The child is a shell, so the tools it started are its children: killing the tree is the
                // only way `npx` does not leave a Node process behind.
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit(2_000);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone, or never really started: nothing left to kill.
        }
    }

    private void KillQuietly()
    {
        if (_process is { } process)
        {
            Kill(process);
        }
    }

    private string ExitNote()
    {
        if (_process is not { } process)
        {
            return string.Empty;
        }

        try
        {
            return process.HasExited
                ? string.Create(CultureInfo.InvariantCulture, $" (the server exited with code {process.ExitCode})")
                : string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string ClientVersion { get; } =
        typeof(McpServerConnection).Assembly.GetName().Version?.ToString() ?? "0.1.0";
}



