using System.Diagnostics;
using System.Globalization;
using SharpCoreAgent.Domain.Index;

namespace SharpCoreAgent.Infrastructure.McpServer;

/// <summary>
/// Brings the index up to date before a call answers, at most once per interval, and never at the cost of the
/// answer.
/// </summary>
/// <remarks>
/// Two budgets, both from <see cref="McpServerOptions"/>: a three-second ceiling for one refresh, and a
/// five-second gap between two of them. An agent asking six related questions in a row is one editing session,
/// not six reasons to walk the working tree, and the interval is what makes the first question pay for the walk
/// and the other five read what it found.
/// <para>
/// Freshness is information, never an error (FR-013). No index, another writer holding the lock, a corrupt file
/// or a spent budget all end the same way: the call answers from the index that is there and the tool's own
/// heading says what state it is in. Only cancellation travels, because the caller asked for it.
/// </para>
/// <para>
/// The maintainer is opened per refresh and released afterwards, for the same reason
/// <see cref="LeasedCodeIndex"/> opens per read: the status a build reads leaves a database handle behind, and a
/// server that kept it would stop a build in another process between calls.
/// </para>
/// </remarks>
public sealed class IndexRefreshGate : IMcpCallGate
{
    private readonly Func<ICodeIndexMaintainer> _open;
    private readonly McpServerOptions _options;
    private readonly Action<string>? _log;
    private readonly TimeProvider _time;
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>
    /// Creates the gate one session uses.
    /// </summary>
    /// <param name="open">Opens a maintainer for one refresh; what it returns is released afterwards.</param>
    /// <param name="options">The refresh switch, ceiling and interval.</param>
    /// <param name="log">Receives one line per refresh and per skipped refresh, or null to stay quiet.</param>
    /// <param name="time">The clock the interval is measured on; the system's by default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="open"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">The options do not validate.</exception>
    public IndexRefreshGate(
        Func<ICodeIndexMaintainer> open,
        McpServerOptions options,
        Action<string>? log = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(options);

        _open = open;
        _options = options.Validate();
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    /// <remarks>
    /// A refresh that will not happen is decided here rather than by the maintainer: the gate is the only place
    /// that knows both how long ago the last attempt was and whether the developer asked for none at all.
    /// </remarks>
    public async ValueTask PrepareAsync(CancellationToken cancellationToken)
    {
        if (!_options.Refresh)
        {
            // Said once by the command's ready line, not on every call: a server that printed this per request
            // would bury the one diagnostic a developer needs to see.
            return;
        }

        var now = _time.GetUtcNow();
        var since = now - _last;

        if (since < _options.RefreshInterval)
        {
            _log?.Invoke(string.Create(
                CultureInfo.InvariantCulture,
                $"refresh: skipped, the last attempt was {since.TotalSeconds:0.0} s ago"));

            return;
        }

        // Stamped before the attempt, deliberately: the point of the interval is that a refresh which fails is
        // not retried by every call that follows it, because a failing refresh costs the full budget every time.
        _last = now;

        var started = Stopwatch.GetTimestamp();

        try
        {
            var maintainer = _open();
            IndexBuildReport report;

            try
            {
                report = await maintainer
                    .EnsureFreshAsync(_options.RefreshBudget, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                await ReleaseAsync(maintainer).ConfigureAwait(false);
            }

            Report(report, Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The call was cancelled while its refresh was running: the call path turns this into "no answer",
            // which is what a client that cancelled asked for.
            throw;
        }
        catch (Exception ex)
        {
            // Everything else is the workspace talking: a read-only checkout, a full disk, another writer, an
            // index this build cannot read. None of it may turn a readable answer into an error.
            _log?.Invoke($"refresh: did not run: {ex.Message}");
        }
    }

    /// <summary>
    /// Logs what one refresh did.
    /// </summary>
    /// <param name="report">The maintainer's report.</param>
    /// <param name="elapsed">How long the refresh took, by the wall clock.</param>
    private void Report(IndexBuildReport report, TimeSpan elapsed)
    {
        var changed = report.Added + report.Changed + report.Removed;

        _log?.Invoke(string.Create(
            CultureInfo.InvariantCulture,
            $"refresh: {report.Status.Freshness.ToString().ToLowerInvariant()}, {changed} changed, {report.Unchanged} unchanged, {elapsed.TotalSeconds:0.0} s"));
    }

    /// <summary>
    /// Closes the maintainer this refresh opened.
    /// </summary>
    /// <param name="maintainer">The maintainer the factory returned.</param>
    /// <returns>A task that completes when the handle is closed.</returns>
    /// <remarks>
    /// <see cref="ICodeIndexMaintainer"/> is a write port and says nothing about disposal, so both shapes are
    /// accepted: the shipped index closes asynchronously, and a test double may close nothing.
    /// </remarks>
    private static async ValueTask ReleaseAsync(ICodeIndexMaintainer maintainer)
    {
        if (maintainer is IAsyncDisposable asynchronous)
        {
            await asynchronous.DisposeAsync().ConfigureAwait(false);
        }
        else if (maintainer is IDisposable synchronous)
        {
            synchronous.Dispose();
        }
    }
}
