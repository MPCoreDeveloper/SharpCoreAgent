using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Infrastructure.McpServer;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Implements <c>sharpcoreagent mcp serve</c>: one stdio MCP server for one workspace, until stdin ends.
/// </summary>
/// <remarks>
/// The command is the process's plumbing and nothing else. It opens the workspace, builds the container, writes
/// the one ready line to stderr and hands the loop the token Ctrl+C sets. Every protocol decision belongs to
/// <see cref="McpServerSession"/> and every index decision to the tools, which is what lets the same session be
/// tested over two memory streams.
/// <para>
/// Nothing here writes to stdout except the session: a banner on stdout would be the first token of a JSON-RPC
/// conversation, and the client would spend the rest of the session resynchronising.
/// </para>
/// </remarks>
internal static class McpCommand
{
    /// <summary>
    /// Serves the workspace over a pair of streams.
    /// </summary>
    /// <param name="options">The parsed command line: the workspace, the refresh switch and verbosity.</param>
    /// <param name="input">What the client writes; read until it ends.</param>
    /// <param name="output">Where answers go, one JSON object per line.</param>
    /// <param name="error">Where the ready line and the diagnostics go.</param>
    /// <param name="cancellationToken">Cancels the loop; Ctrl+C and a closed stdin both end it at 0.</param>
    /// <returns>The process exit code: 0 for end of input or cancellation, 2 for an unusable workspace.</returns>
    /// <remarks>
    /// The streams are parameters rather than <see cref="Console"/> reads so that a test can run the whole
    /// command — container, ready line, loop and exit code — without a child process.
    /// </remarks>
    internal static async Task<int> ExecuteAsync(
        CliOptions options,
        Stream input,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!CliHost.TryOpenWorkspace(options, error, out var opened))
        {
            return ExitCodes.Usage;
        }

        var workspace = opened!;
        var settings = new McpServerOptions { Refresh = !options.NoRefresh };
        var services = new ServiceCollection();

        // Registered before the server so the session it builds answers on these streams; the default is the
        // process's own stdio, which is what the real command reaches.
        services.AddSingleton(new McpServerStreams(input, output));
        services.AddMcpServer(workspace, settings, options.Verbose ? error.WriteLine : null);

        await using var provider = services.BuildServiceProvider();
        await using var session = provider.GetRequiredService<McpServerSession>();

        await WriteReadyLineAsync(session, provider, workspace, options, error, cancellationToken).ConfigureAwait(false);

        return await session.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the one line a client may log before the first answer.
    /// </summary>
    /// <param name="session">The session, for the published tool count.</param>
    /// <param name="provider">The container, for the index the line describes.</param>
    /// <param name="workspace">The workspace being served.</param>
    /// <param name="options">The parsed command line, read for the refresh note.</param>
    /// <param name="error">Where the line goes.</param>
    /// <param name="cancellationToken">Cancels the status read.</param>
    /// <returns>A task that completes when the line has been written.</returns>
    /// <remarks>
    /// A status, not a promise: a workspace nobody indexed is served too, and the line says <c>missing</c> so the
    /// developer learns it here rather than from an agent's puzzled first answer. The read goes through the leased
    /// index, so it leaves no handle open.
    /// </remarks>
    private static async Task WriteReadyLineAsync(
        McpServerSession session,
        IServiceProvider provider,
        Workspace workspace,
        CliOptions options,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var status = await provider
            .GetRequiredService<ICodeIndex>()
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);

        await error.WriteLineAsync(
            string.Create(
                CultureInfo.InvariantCulture,
                $"sharpcoreagent mcp serve: {workspace.Root} · index {IndexCommand.State(status.Freshness)} ({status.Files} files, {status.Chunks} passages) · {session.PublishedToolCount} tools")
                .AsMemory(),
            cancellationToken).ConfigureAwait(false);

        if (options.NoRefresh)
        {
            await error.WriteLineAsync(
                "refresh is off (--no-refresh): answers describe the index as it is".AsMemory(),
                cancellationToken).ConfigureAwait(false);
        }
    }
}
