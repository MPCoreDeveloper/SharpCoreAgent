using System.Diagnostics;
using System.Globalization;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Index;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// The write side of the index: one build at a time, one build id per run, and never a throw because another
/// process got there first.
/// </summary>
/// <remarks>
/// The maintainer owns three things a reader does not: the writer lock, the build id and the clock. Every write
/// path in M1 enters through <see cref="EnsureFreshAsync"/>, which is what makes "a second process sees
/// <see cref="IndexFreshness.Building"/>" a property of the design instead of a race someone has to reason
/// about later.
/// <para>
/// The interface it grows into is <see cref="ICodeIndexMaintainer"/>: rebuild, verify and prune land with the
/// write path (T012, T014, T015), and shipping them as methods that quietly do nothing would be worse than not
/// having them yet.
/// </para>
/// </remarks>
internal sealed class SharpCoreDbIndexMaintainer : IAsyncDisposable
{
    /// <summary>The line a caller prints when another process is mid-build.</summary>
    internal const string BuildingNote = "another SharpCoreAgent process is building this index; the last complete build is still readable";

    private readonly Lock _gate = new();
    private readonly IndexPaths _paths;
    private readonly SharpCoreDbCodeIndex _reader;
    private readonly string? _encryptionPassword;

    private IndexWriterLock? _writer;
    private string? _buildId;

    private SharpCoreDbIndexMaintainer(IndexPaths paths, SharpCoreDbCodeIndex reader, string? encryptionPassword)
    {
        _paths = paths;
        _reader = reader;
        _encryptionPassword = encryptionPassword;
    }

    /// <summary>Gets the paths this maintainer writes.</summary>
    internal IndexPaths Paths => _paths;

    /// <summary>Gets the id of the run in progress, or of the last run this instance finished.</summary>
    internal string? BuildId
    {
        get
        {
            lock (_gate)
            {
                return _buildId;
            }
        }
    }

    /// <summary>Gets a value indicating whether this instance currently holds the writer lock.</summary>
    internal bool HoldsWriterLock
    {
        get
        {
            lock (_gate)
            {
                return _writer is not null;
            }
        }
    }

    /// <summary>
    /// Opens a maintainer for one workspace. Nothing is created or locked until the first build.
    /// </summary>
    /// <param name="workspace">The workspace whose index is written.</param>
    /// <param name="encryptionPassword">A password when the index is encrypted; otherwise <see langword="null"/>.</param>
    /// <returns>The maintainer, which the caller disposes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> is null.</exception>
    public static SharpCoreDbIndexMaintainer For(Workspace workspace, string? encryptionPassword = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        var reader = SharpCoreDbCodeIndex.For(workspace, encryptionPassword);
        return new SharpCoreDbIndexMaintainer(IndexPaths.For(workspace), reader, encryptionPassword);
    }

    /// <summary>
    /// Brings the index up to date, waiting no longer than <paramref name="budget"/> for the writer lock.
    /// </summary>
    /// <param name="budget">The time the caller is willing to spend; the lock wait is part of it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>
    /// The report of the run, or a report whose status is <see cref="IndexFreshness.Building"/> when another
    /// writer kept the lock for the whole budget.
    /// </returns>
    public async ValueTask<IndexBuildReport> EnsureFreshAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        if (!await AcquireWriterAsync(budget, cancellationToken))
        {
            var blocked = await _reader.GetStatusAsync(cancellationToken);
            return new IndexBuildReport
            {
                Status = blocked with { Freshness = IndexFreshness.Building },
                Incomplete = true,
                ElapsedMilliseconds = Elapsed(started),
                Notes = [BuildingNote],
            };
        }

        try
        {
            return Build(started, cancellationToken);
        }
        finally
        {
            ReleaseWriter();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        ReleaseWriter();
        _reader.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<bool> AcquireWriterAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_writer is not null)
            {
                // Re-entering from the same instance is not a second writer: the lock is already ours.
                return true;
            }
        }

        var writer = await IndexWriterLock.TryAcquireAsync(_paths, budget, cancellationToken);

        lock (_gate)
        {
            _writer = writer;
            return writer is not null;
        }
    }

    private void ReleaseWriter()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private IndexBuildReport Build(long started, CancellationToken cancellationToken)
    {
        _buildId = IndexBuildId.NewId();

        _paths.EnsureDirectoryCreated();
        using var lease = IndexHandleCache.Acquire(_paths, _encryptionPassword, createIfMissing: true);
        IndexSchema.EnsureCreated(lease.Database.Database, _paths);

        // T012 fills this in: scan, extract, chunk, embed and write, one file per transaction. Until then the
        // honest report is an index that exists and holds nothing, said out loud rather than implied by zeros.
        cancellationToken.ThrowIfCancellationRequested();

        _reader.InvalidateStatus();
        var status = _reader.GetStatus();

        return new IndexBuildReport
        {
            Status = status,
            ElapsedMilliseconds = Elapsed(started),
            Notes =
            [
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"build {_buildId}: the schema is in place and no file has been extracted yet"),
            ],
        };
    }

    private static long Elapsed(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
