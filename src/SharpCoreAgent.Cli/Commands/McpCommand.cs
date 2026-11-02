using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SharpCoreAgent.DependencyInjection;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Infrastructure.McpServer;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Implements <c>sharpcoreagent mcp</c>: the stdio server for one workspace, and the snippet that wires a client to it.
/// </summary>
/// <remarks>
/// The command is the process's plumbing and nothing else. <c>serve</c> opens the workspace, builds the container,
/// writes the one ready line to stderr and hands the loop the token Ctrl+C sets. Every protocol decision belongs to
/// <see cref="McpServerSession"/> and every index decision to the tools, which is what lets the same session be
/// tested over two memory streams. <c>config</c> is the opposite: it opens nothing, reads nothing and prints one block.
/// <para>
/// Nothing here writes to stdout except what was asked for: a banner in front of the protocol would be the first token
/// of a JSON-RPC conversation, and the client would spend the rest of the session resynchronising.
/// </para>
/// </remarks>
internal static class McpCommand
{
    /// <summary>
    /// Runs the verb the command line asked for.
    /// </summary>
    /// <param name="options">The parsed command line: the action, the workspace, the refresh switch and verbosity.</param>
    /// <param name="input">What the client writes; read until it ends.</param>
    /// <param name="output">Where answers go, one JSON object per line — or the snippet.</param>
    /// <param name="error">Where the ready line and the diagnostics go.</param>
    /// <param name="cancellationToken">Cancels the loop; Ctrl+C and a closed stdin both end it at 0.</param>
    /// <returns>The process exit code: 0 for a printed snippet, end of input or cancellation, 2 for an unusable workspace.</returns>
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

        return options.McpAction switch
        {
            McpAction.Config => await WriteSnippetAsync(options, output, error, cancellationToken).ConfigureAwait(false),
            McpAction.Bench => await BenchAsync(options, output, error, cancellationToken).ConfigureAwait(false),
            _ => await ServeAsync(options, input, output, error, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Runs the measurement the round's numbers come from.
    /// </summary>
    /// <param name="options">The parsed command line: the workspace.</param>
    /// <param name="output">Where the table goes.</param>
    /// <param name="error">Where the header and the parity failures go.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The exit code the bench chose.</returns>
    /// <remarks>
    /// The stream is wrapped without a byte-order mark and left open: a redirect of this table into a file is the
    /// intended use, and a BOM in front of the first character would be the first thing a reader saw.
    /// </remarks>
    private static async Task<int> BenchAsync(
        CliOptions options,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(false), bufferSize: 4096, leaveOpen: true);

        var exitCode = await McpBench.ExecuteAsync(options, writer, error, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

        return exitCode;
    }

    /// <summary>
    /// Serves the workspace over a pair of streams.
    /// </summary>
    /// <param name="options">The parsed command line: the workspace, the refresh switch and verbosity.</param>
    /// <param name="input">What the client writes; read until it ends.</param>
    /// <param name="output">Where answers go, one JSON object per line.</param>
    /// <param name="error">Where the ready line and the diagnostics go.</param>
    /// <param name="cancellationToken">Cancels the loop; Ctrl+C and a closed stdin both end it at 0.</param>
    /// <returns>The process exit code: 0 for end of input or cancellation, 2 for an unusable workspace.</returns>
    private static async Task<int> ServeAsync(
        CliOptions options,
        Stream input,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
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
                $"sharpcoreagent mcp serve: {workspace.Root} · index {IndexStatusDocument.State(status.Freshness)} ({status.Files} files, {status.Chunks} passages) · {session.PublishedToolCount} tools")
                .AsMemory(),
            cancellationToken).ConfigureAwait(false);

        if (options.NoRefresh)
        {
            await error.WriteLineAsync(
                "refresh is off (--no-refresh): answers describe the index as it is".AsMemory(),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Prints the block that adds this server to another agent's configuration, or writes it into that file.
    /// </summary>
    /// <param name="options">The parsed command line: the client, the write switch and the workspace the note names.</param>
    /// <param name="output">Where the block goes, and nothing else — or nothing at all, under <c>--write</c>.</param>
    /// <param name="error">Where the one line naming the file goes.</param>
    /// <param name="cancellationToken">Cancels the write, which only happens if the reader stopped reading.</param>
    /// <returns>
    /// The process exit code: 0 for a block printed or a file written, 7 when the file could not be read or merged.
    /// </returns>
    /// <remarks>
    /// No workspace is opened and no index is read: the command answers a question about configuration, and a
    /// developer wiring up a client should not need an index to exist first. The bytes are written without a
    /// byte-order mark, because the common case is a redirect into the file itself.
    /// <para>
    /// <c>--write</c> prints nothing on stdout. The block was not printed for a redirect, and a caller that pipes this
    /// command into a file would otherwise append a second copy to the one just merged.
    /// </para>
    /// </remarks>
    private static async Task<int> WriteSnippetAsync(
        CliOptions options,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        // FinishMcp refuses `mcp config` without a client before the command runs. The guard stays because the
        // alternative — a default — would print a block for a client nobody named, and that is the one mistake a
        // snippet command must not make.
        var client = options.McpClient
            ?? throw new CliUsageException(
                "mcp config needs --client.",
                "Use --client claude, --client vscode, --client visualstudio or --client codex.");

        var (command, arguments) = McpClientSnippets.Invocation();
        var destination = McpClientSnippets.Destination(client, Path.GetFullPath(options.Workspace));

        if (options.McpConfigWrite)
        {
            try
            {
                var (_, note) = McpConfigWriter.Write(client, destination, command, arguments);

                await error.WriteLineAsync(note.AsMemory(), cancellationToken).ConfigureAwait(false);
                return ExitCodes.Success;
            }
            catch (McpConfigWriteException exception)
            {
                // Exit 7, and the file is as it was: a developer who reads the message and opens the path sees what
                // this command saw, which is the only way a refusal about a file is useful.
                await error
                    .WriteLineAsync($"{McpClientSnippets.NotePrefix}{exception.Message}".AsMemory(), cancellationToken)
                    .ConfigureAwait(false);

                return ExitCodes.Configuration;
            }
        }

        await output
            .WriteAsync(Encoding.UTF8.GetBytes(McpClientSnippets.Render(client, command, arguments)), cancellationToken)
            .ConfigureAwait(false);

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);

        await error
            .WriteLineAsync(McpClientSnippets.Note(client, destination).AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        return ExitCodes.Success;
    }
}
