using System.Diagnostics;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// The single-writer lock: one exclusive handle on <c>.lock</c>, held for the length of a build.
/// </summary>
/// <remarks>
/// Exclusivity is a file handle opened with <see cref="FileShare.None"/> rather than a named mutex, because a
/// handle is the one primitive Windows and Unix agree on and because the file doubles as a diagnostic: the
/// presence of <c>.lock</c> beside the database says a build is running somewhere. The wait is on the handle,
/// never on the file's existence, so a lock file left behind by a killed process costs the next run one open
/// call instead of a deadlock.
/// </remarks>
internal sealed class IndexWriterLock : IDisposable
{
    private const int InitialDelayMilliseconds = 25;
    private const int MaximumDelayMilliseconds = 250;

    private static readonly Lock Gate = new();
    private static readonly HashSet<string> HeldByThisProcess = new(StringComparer.OrdinalIgnoreCase);

    private FileStream? _stream;
    private readonly string _path;

    private IndexWriterLock(FileStream stream, string path)
    {
        _stream = stream;
        _path = path;
    }

    /// <summary>
    /// Determines whether a writer in another process (or an unregistered handle in this one) holds the lock.
    /// </summary>
    /// <param name="paths">The paths the index owns.</param>
    /// <returns><see langword="true"/> when the lock belongs to somebody else.</returns>
    /// <remarks>
    /// The question this answers is "may I write now?", not "is the file open somewhere?", so the handle this
    /// process holds is excluded. Without that exclusion a build would see its own lock and report itself as
    /// <see cref="SharpCoreAgent.Domain.Index.IndexFreshness.Building"/> for its whole run. The probe takes the lock for the
    /// instant it needs to look and gives it back; an absent lock file is answered without creating one.
    /// </remarks>
    public static bool IsHeldByAnotherWriter(IndexPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        lock (Gate)
        {
            if (HeldByThisProcess.Contains(paths.LockPath))
            {
                return false;
            }

            if (!File.Exists(paths.LockPath))
            {
                return false;
            }

            if (!TryOpen(paths, out var stream))
            {
                return true;
            }

            stream!.Dispose();
            return false;
        }
    }

    /// <summary>
    /// Waits up to <paramref name="wait"/> for the writer lock.
    /// </summary>
    /// <param name="paths">The paths the index owns.</param>
    /// <param name="wait">The longest the caller is willing to wait; <see cref="TimeSpan.Zero"/> is a single try.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The held lock, or <see langword="null"/> when another writer still had it.</returns>
    /// <remarks>
    /// The retry backs off from 25 to 250 milliseconds: a build that takes a second is polled a handful of
    /// times, and a build that takes a minute does not spin. Anything other than a sharing violation is a real
    /// failure and is not translated into "busy".
    /// </remarks>
    public static async ValueTask<IndexWriterLock?> TryAcquireAsync(
        IndexPaths paths,
        TimeSpan wait,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        paths.EnsureDirectoryCreated();

        var stopwatch = Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(InitialDelayMilliseconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Opening and registering happen inside the gate that the probe takes, so a status read can never
            // see our handle before it can see that the handle is ours.
            lock (Gate)
            {
                if (TryOpen(paths, out var stream))
                {
                    HeldByThisProcess.Add(paths.LockPath);
                    return new IndexWriterLock(stream!, paths.LockPath);
                }
            }

            var remaining = wait - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            await Task.Delay(remaining < delay ? remaining : delay, cancellationToken);

            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, MaximumDelayMilliseconds));
        }
    }

    private static bool TryOpen(IndexPaths paths, out FileStream? stream)
    {
        try
        {
            stream = new FileStream(paths.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            // A sharing violation is the lock working, not a fault: another handle has the file open. Every
            // other IO failure is a real one and is left to the caller.
            stream = null;
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (Gate)
        {
            _stream?.Dispose();
            _stream = null;
            HeldByThisProcess.Remove(_path);
        }
    }
}
