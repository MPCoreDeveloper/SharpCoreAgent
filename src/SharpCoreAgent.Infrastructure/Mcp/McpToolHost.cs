namespace SharpCoreAgent.Infrastructure.Mcp;

using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Tools;

/// <summary>
/// Where a server's stderr goes, changing at most once while the process runs.
/// </summary>
/// <remarks>
/// The container builds the host before the command line has decided whether <c>--verbose</c> was given, so
/// the sink is mutable rather than a constructor argument. A message written before the sink is set is
/// dropped, which is the right answer: nobody was listening.
/// </remarks>
internal sealed class McpLogSink
{
    private Action<string>? _sink;

    /// <summary>Sets where messages go, or clears it.</summary>
    /// <param name="sink">The writer, or null to drop everything.</param>
    public void Set(Action<string>? sink) => Volatile.Write(ref _sink, sink);

    /// <summary>Writes one diagnostic line, if anyone is listening.</summary>
    /// <param name="message">The line, already naming the server.</param>
    public void Write(string message) => Volatile.Read(ref _sink)?.Invoke(message);
}

/// <summary>
/// Every MCP server this process started, and the tools they offer.
/// </summary>
/// <remarks>
/// The host is a container singleton because it owns child processes that outlive any one tool call, and it
/// is the only type in the product that starts a server. Servers are started in the order the flags were
/// given, so the prefixes a developer sees match the command line they typed.
/// <para>
/// <see cref="Started"/> is synchronous and idempotent on purpose: a tool list is a construction-time fact,
/// and the registry is built from it. The CLI starts the host before resolving anything else, so a broken
/// server is a message and exit 2 instead of an exception several frames into a turn.
/// </para>
/// </remarks>
public sealed class McpToolHost : IDisposable, IAsyncDisposable
{
    private readonly McpClientOptions _options;
    private readonly IChatModelResolver? _models;
    private readonly McpLogSink _sink = new();
    private readonly List<McpServerConnection> _connections = [];
    private readonly List<ITool> _tools = [];
    private readonly object _gate = new();
    private bool _started;
    private bool _disposed;

    /// <summary>
    /// Creates the host for the servers a developer named.
    /// </summary>
    /// <param name="servers">The command lines, in the order the flags were given.</param>
    /// <param name="options">The ceilings every connection works within.</param>
    /// <param name="models">The resolver sampling uses, or null when the host did not provide one.</param>
    /// <param name="urls">The remote streamable-HTTP endpoints, in the order the flags were given.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A command line is blank, or a URL is not absolute http or https.</exception>
    public McpToolHost(
        IEnumerable<string> servers,
        McpClientOptions options,
        IChatModelResolver? models = null,
        IEnumerable<string>? urls = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        _models = models;

        // A stem that repeats gets `_2`, `_3` … so two `npx` servers keep distinct tool names without the
        // developer having to invent one.
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var commandLine in servers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

            var stem = McpLauncher.Stem(commandLine);
            var count = Next(seen, stem);
            _connections.Add(new McpServerConnection(
                McpToolName.ForServer(stem, count),
                commandLine,
                _options,
                _sink.Write));
        }

        foreach (var url in urls ?? [])
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(url);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException($"'{url}' is not an absolute http or https MCP URL", nameof(urls));
            }

            var count = Next(seen, endpoint.Host);
            _connections.Add(new McpServerConnection(
                McpToolName.ForServer(endpoint.Host, count),
                endpoint,
                _options,
                _sink.Write));
        }
    }

    private static int Next(Dictionary<string, int> seen, string stem)
    {
        var key = McpToolName.Fold(stem);
        seen.TryGetValue(key, out var count);
        count++;
        seen[key] = count;
        return count;
    }

    /// <summary>Gets the connections, in the order the flags were given.</summary>
    public IReadOnlyList<McpServerConnection> Servers => _connections;

    /// <summary>Gets the tools the servers offered, in server order.</summary>
    public IReadOnlyList<ITool> Tools => _tools;

    /// <summary>
    /// Sets where server stderr is echoed while <c>--verbose</c> is in force.
    /// </summary>
    /// <param name="sink">The writer, or null to discard.</param>
    public void Verbose(Action<string>? sink) => _sink.Set(sink);

    /// <summary>
    /// Starts every server, in order, and builds one tool per entry they offered.
    /// </summary>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>The start, once every server has answered.</returns>
    /// <exception cref="McpStartupException">A server would not start, or offered no tools; none are left running.</exception>
    /// <exception cref="OperationCanceledException">The caller cancelled.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _tools.Clear();
        }

        try
        {
            foreach (var connection in _connections)
            {
                await connection.StartAsync(cancellationToken).ConfigureAwait(false);
                AddTools(connection);
            }

            lock (_gate)
            {
                _started = true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Atomic: a half-started set of servers is worse than none, because which tools a later turn sees
            // would depend on which flag happened to fail.
            await StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Starts the host if it is not started, blocking, and returns it.
    /// </summary>
    /// <returns>This host, started.</returns>
    /// <remarks>
    /// Blocking is deliberate: the registry factory needs the tool list while it is being constructed, and a
    /// registry that is right or wrong depending on resolution order is a bug that shows up as a missing tool
    /// in one command and not another.
    /// </remarks>
    /// <exception cref="McpStartupException">A server would not start.</exception>
    public McpToolHost Started()
    {
        StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return this;
    }

    /// <summary>
    /// Stops every server, killing each child's process tree.
    /// </summary>
    /// <returns>The stop.</returns>
    public async Task StopAsync()
    {
        List<McpServerConnection> connections;
        lock (_gate)
        {
            connections = [.. _connections];
        }

        foreach (var connection in connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Synchronous on purpose: disposal runs from the container, and the only work left is killing
        // children, which must happen before the process exits rather than some frames later.
        foreach (var connection in _connections)
        {
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
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

        foreach (var connection in _connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void AddTools(McpServerConnection connection)
    {
        // Folded names can collide inside one server — `getUser` and `get_user` are the same tool name — and a
        // later one is suffixed rather than dropped, so no capability disappears without a word.
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var info in connection.Tools)
        {
            var name = McpToolName.ForTool(connection.Name, info.Name);
            var unique = name;
            var ordinal = 1;
            while (!used.Add(unique))
            {
                unique = McpToolName.WithSuffix(name, ++ordinal);
            }

            lock (_gate)
            {
                _tools.Add(new McpTool(unique, info, connection, _options, _models));
            }
        }

        if (connection.HasResources)
        {
            AddCatalogTool(connection, "resources", used, name => new McpResourceTool(name, connection, _options));
        }

        if (connection.HasPrompts)
        {
            AddCatalogTool(connection, "prompts", used, name => new McpPromptTool(name, connection, _options));
        }
    }

    private void AddCatalogTool(
        McpServerConnection connection,
        string suffix,
        HashSet<string> used,
        Func<string, ITool> create)
    {
        var name = McpToolName.ForTool(connection.Name, suffix);
        var unique = name;
        var ordinal = 1;
        while (!used.Add(unique))
        {
            unique = McpToolName.WithSuffix(name, ++ordinal);
        }

        lock (_gate)
        {
            _tools.Add(create(unique));
        }
    }
}

