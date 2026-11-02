using System.Globalization;
using SharpCoreAgent.Indexing;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Implements <c>sharpcoreagent ui --serve</c>: this workspace's index as a page on a loopback port.
/// </summary>
/// <remarks>
/// The command is deliberately thin. It opens the workspace and the index the other commands open, hands them to
/// <see cref="UiServer"/>, says where the panel is, and serves until the process is interrupted. Nothing here starts a
/// process, opens a socket to anywhere, or asks a question: the panel reads an index that another command built.
/// <para>
/// Exit `0` on Ctrl+C, the way <c>mcp serve</c> ends, and exit `2` when the port cannot be bound, because a port the
/// platform will not give up is a command line that cannot be honoured — the same answer a port outside the range gets
/// before anything is opened (D9).
/// </para>
/// </remarks>
internal static class UiCommand
{
    /// <summary>
    /// Serves the panel, writing where it is to the process's own streams.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="cancellationToken">Cancels the listener and ends the command.</param>
    /// <returns>The documented exit code.</returns>
    internal static Task<int> ExecuteAsync(CliOptions options, CancellationToken cancellationToken) =>
        ExecuteAsync(options, Console.Out, Console.Error, cancellationToken);

    /// <summary>
    /// Serves the panel, writing where it is to two streams.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="output">Where the <c>--json</c> object goes: stdout for the process, a buffer for a test.</param>
    /// <param name="error">Where the URL, the note and any failure go: stderr for the process, a buffer for a test.</param>
    /// <param name="cancellationToken">Cancels the listener and ends the command.</param>
    /// <returns>The documented exit code.</returns>
    /// <remarks>
    /// The writers are parameters for the reason <see cref="IndexCommand"/>'s are: where the URL is printed is part of
    /// the contract, and a test that cannot see it has to drive the process instead. stdout stays parseable — with
    /// <c>--json</c> it carries one object and nothing else — and every sentence goes to stderr.
    /// </remarks>
    internal static async Task<int> ExecuteAsync(
        CliOptions options,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (!CliHost.TryOpenWorkspace(options, error, out var opened))
        {
            return ExitCodes.Usage;
        }

        var workspace = opened!;

        // The same handle `index` and `mcp serve` open, in this process: FR-006 rules out reading the status by running
        // `index --status --json` as a child, and holding one handle is also what keeps the panel's numbers and a
        // terminal's numbers the same numbers.
        await using var index = SharpCoreAgentIndex.Open(workspace);

        if (!UiServer.TryStart(
                options.UiPort,
                index,
                workspace,
                options.UiRefresh,
                TimeSpan.FromMilliseconds(options.IndexBudgetMs),
                error,
                out var server))
        {
            return ExitCodes.Usage;
        }

        using (server)
        {
            Announce(server!.Endpoint, workspace.Root, options, output, error);
            await server.ServeAsync(cancellationToken).ConfigureAwait(false);
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// Says where the panel is, and how to open it.
    /// </summary>
    /// <param name="endpoint">The address that is listening.</param>
    /// <param name="workspaceRoot">The workspace whose index is being served.</param>
    /// <param name="options">The parsed command line, for <c>--json</c> and <c>--refresh</c>.</param>
    /// <param name="output">Where the JSON object goes, when one was asked for.</param>
    /// <param name="error">Where the sentences go.</param>
    /// <remarks>
    /// The URL is the first line on its own, so a person can copy it and a log can be read from the top. Opening it is the
    /// developer's action (FR-005, D7): the note names the two editors' own browsers because that is the workflow this
    /// round exists for, and it says Ctrl+C because a server that does not say how to stop is a server people kill.
    /// </remarks>
    private static void Announce(
        UiEndpoint endpoint,
        string workspaceRoot,
        CliOptions options,
        TextWriter output,
        TextWriter error)
    {
        if (options.Json)
        {
            output.WriteLine(endpoint.ToJson(workspaceRoot, options.UiRefresh));
        }

        error.WriteLine(endpoint.Url);
        error.WriteLine("note: open it in a browser, or in VS Code's Simple Browser (Ctrl+Shift+P → Simple Browser: Show) or a Visual Studio browser window");
        error.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"note: {IndexStatusDocument.Slash(workspaceRoot)} · index {(options.UiRefresh ? "read again before each answer" : "read as it is; --refresh walks it before each answer")}"));
        error.WriteLine("note: Ctrl+C stops the panel");
    }
}
