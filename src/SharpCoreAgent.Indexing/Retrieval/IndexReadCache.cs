using SharpCoreAgent.Domain.Index;

namespace SharpCoreAgent.Indexing.Retrieval;

/// <summary>
/// What one index database remembers between calls: the build that was loaded, and the status that was computed
/// for the file as it was when it was computed.
/// </summary>
/// <remarks>
/// Both answers describe the file, not the call, so both belong to the file. A reader is built per call by the
/// lease — that is what lets a build in a terminal run beside an idle editor — and anything this class held
/// privately would be thrown away on every call: the snapshot costs 3.9 s to load and the status costs 1.3 s to
/// compute on a large index, and paying either twice for one unchanged file is the whole defect this type fixes.
/// <para>
/// The handle is deliberately <em>not</em> in here. It is the one thing a per-call instance must release, and
/// <see cref="IndexHandleCache"/> already shares it for the callers that hold one.
/// </para>
/// </remarks>
internal sealed class IndexReadState
{
    /// <summary>Creates the state of one database.</summary>
    /// <param name="reader">The reader that will hold the loaded build.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is null.</exception>
    internal IndexReadState(IndexReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Reader = reader;
    }

    /// <summary>Gets the reader that holds the loaded build for every caller reading this database.</summary>
    internal IndexReader Reader { get; }

    /// <summary>
    /// Gets or sets the status computed for this database, with the stamp that says which file it describes.
    /// </summary>
    /// <remarks>
    /// One reference, written atomically, deliberately: two calls computing a status at the same time must not be
    /// able to leave a status paired with another call's stamp. The last writer wins and both are right.
    /// </remarks>
    internal IndexStatusMemo? Status { get; set; }
}

/// <summary>
/// A computed status and the file stamp it was computed for.
/// </summary>
/// <param name="Stamp">The database file's length and write time when the status was read.</param>
/// <param name="Status">The status that stamp produced.</param>
internal sealed record IndexStatusMemo(FileStamp Stamp, IndexStatus Status);

/// <summary>
/// One loaded build and one computed status per index database, per process, shared by every reader that opens it.
/// </summary>
/// <remarks>
/// A snapshot is the expensive half of a read and a handle is the cheap half. Loading this repository's index costs
/// 3 917 ms and re-reading a loaded one costs nothing; computing its status costs 1 347 ms and re-reading that costs
/// nothing either. Opening the same database costs one millisecond, and releasing it between calls is what lets a
/// build in a terminal run beside an idle editor. This cache keeps the two expensive answers and never the handle:
/// the handle a call opens is still opened per call and released in a <c>finally</c>.
/// <para>
/// Freshness is the two answers' own business and is unchanged. <see cref="IndexReader.Load"/> reads
/// <c>meta['build_id']</c> from the handle it is given and reloads when the id differs from the one it holds, and
/// <see cref="IndexReadState.Status"/> carries the file stamp it was computed for. This type only decides which
/// state a caller gets; it has no timer, no watcher and no invalidation of its own.
/// </para>
/// <para>
/// The map is bounded because these answers are tens of megabytes on a large workspace. Four entries is not a tuned
/// number: a process opens one index today, an <c>mcp bench</c> run opens a second in-process and a third in its
/// child, and a fifth distinct workspace is a different program. An entry whose database file is gone — every test
/// sandbox, once its temp directory is deleted — is dropped on the next miss rather than left to a timer.
/// </para>
/// </remarks>
internal static class IndexReadCache
{
    /// <summary>How many databases one process may hold answers for.</summary>
    internal const int MaxEntries = 4;

    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    private static long _clock;

    /// <summary>Gets how many databases this process currently holds answers for.</summary>
    internal static int EntryCount
    {
        get
        {
            lock (Gate)
            {
                return Entries.Count;
            }
        }
    }

    /// <summary>
    /// Returns the state of one index database, creating it the first time and reusing it afterwards.
    /// </summary>
    /// <param name="paths">The paths the index owns; the database path is the key.</param>
    /// <returns>The shared state; the caller never disposes it, because it outlives every call.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    /// <remarks>
    /// A miss makes room before it inserts: entries whose database file no longer exists go first, and the least
    /// recently used goes only if the bound is still reached. A caller that disposes its own index — which the
    /// per-call lease does on every call — therefore cannot take the answers with it.
    /// </remarks>
    internal static IndexReadState For(IndexPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        lock (Gate)
        {
            if (Entries.TryGetValue(paths.DatabasePath, out var existing))
            {
                existing.LastUse = ++_clock;
                return existing.State;
            }

            DropDeadEntries();
            EvictIfFull();

            var state = new IndexReadState(IndexReader.Create());
            Entries[paths.DatabasePath] = new Entry(paths, state, ++_clock);
            return state;
        }
    }

    /// <summary>
    /// Drops what this process remembers about one database, so the next reader for it starts empty.
    /// </summary>
    /// <param name="paths">The paths the index owns.</param>
    /// <returns><see langword="true"/> when an entry was dropped.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="paths"/> is null.</exception>
    /// <remarks>
    /// This is for a caller that knows the answers it holds are wrong — a rebuild that knows the build id did not
    /// change, and the tests that assert this cache's shape. Freshness does not need it: a new <c>build_id</c> and a
    /// new file stamp both do the job on their own.
    /// </remarks>
    internal static bool Forget(IndexPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        lock (Gate)
        {
            return Entries.Remove(paths.DatabasePath);
        }
    }

    /// <summary>Drops everything this process remembers about every database.</summary>
    /// <returns>How many entries were dropped.</returns>
    internal static int ForgetAll()
    {
        lock (Gate)
        {
            var dropped = Entries.Count;
            Entries.Clear();
            return dropped;
        }
    }

    /// <summary>Drops the entries whose database file no longer exists.</summary>
    private static void DropDeadEntries()
    {
        List<string>? dead = null;

        foreach (var (key, entry) in Entries)
        {
            if (!entry.Paths.DatabaseExists)
            {
                (dead ??= []).Add(key);
            }
        }

        if (dead is null)
        {
            return;
        }

        foreach (var key in dead)
        {
            Entries.Remove(key);
        }
    }

    /// <summary>Drops the least recently used entry when the bound is reached.</summary>
    private static void EvictIfFull()
    {
        if (Entries.Count < MaxEntries)
        {
            return;
        }

        string? oldestKey = null;
        var oldestUse = long.MaxValue;

        foreach (var (key, entry) in Entries)
        {
            if (entry.LastUse < oldestUse)
            {
                oldestUse = entry.LastUse;
                oldestKey = key;
            }
        }

        if (oldestKey is not null)
        {
            Entries.Remove(oldestKey);
        }
    }

    /// <summary>One database: the answers remembered for it, the paths they describe, and when it was last asked for.</summary>
    private sealed class Entry(IndexPaths paths, IndexReadState state, long lastUse)
    {
        public IndexPaths Paths { get; } = paths;

        public IndexReadState State { get; } = state;

        public long LastUse { get; set; } = lastUse;
    }
}
