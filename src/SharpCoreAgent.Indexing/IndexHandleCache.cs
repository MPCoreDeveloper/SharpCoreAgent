namespace SharpCoreAgent.Indexing;

/// <summary>
/// One process-wide open handle per index database, handed out as ref-counted leases.
/// </summary>
/// <remarks>
/// Two reasons this exists instead of an open per caller. First, the engine keeps a handle on a single-file
/// database, and a second open of the same file in the same process is at best wasteful and at worst locked
/// against the first. Second, the caller that builds and the caller that queries must see the same rows: with
/// one handle they cannot disagree about a transaction.
/// <para>
/// A lease keeps the handle alive across calls; <see cref="CloseAll"/> is the only way to take it away early,
/// and it exists for the one caller that has to delete the file.
/// </para>
/// </remarks>
internal static class IndexHandleCache
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets how many distinct databases this process currently holds open.</summary>
    internal static int OpenHandleCount
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
    /// Takes a lease on the handle of one workspace, opening it when this is the first lease.
    /// </summary>
    /// <param name="paths">The paths the index owns.</param>
    /// <param name="encryptionPassword">A password when encryption is configured; otherwise <see langword="null"/>.</param>
    /// <param name="createIfMissing">Whether a missing database may be created by this open.</param>
    /// <returns>The lease, which the caller disposes.</returns>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="createIfMissing"/> is <see langword="false"/> and the database does not exist.
    /// </exception>
    public static IndexHandleLease Acquire(IndexPaths paths, string? encryptionPassword, bool createIfMissing)
    {
        ArgumentNullException.ThrowIfNull(paths);

        lock (Gate)
        {
            if (Entries.TryGetValue(paths.DatabasePath, out var existing))
            {
                existing.Leases++;
                return new IndexHandleLease(existing.Database);
            }

            var database = createIfMissing
                ? SharpCoreDbIndexDatabase.OpenOrCreate(paths, encryptionPassword)
                : SharpCoreDbIndexDatabase.OpenExisting(paths, encryptionPassword);

            Entries[paths.DatabasePath] = new Entry(database);
            return new IndexHandleLease(database);
        }
    }

    /// <summary>
    /// Closes every cached handle this process holds, whether or not it is leased.
    /// </summary>
    /// <remarks>
    /// This is the rebuild hammer, not a routine call: a rebuild that replaces the database file cannot delete
    /// it while any handle is open, and there is no way to ask a lease holder to stop. It is safe only under
    /// the writer lock, which is what makes it a one-line call in <c>RebuildAsync</c> and a landmine anywhere
    /// else. Every lease that outlives it reports a disposed database on its next query and reopens lazily.
    /// </remarks>
    /// <param name="paths">The paths the index owns.</param>
    /// <returns><see langword="true"/> when a handle was closed.</returns>
    internal static bool CloseAll(IndexPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        lock (Gate)
        {
            if (!Entries.Remove(paths.DatabasePath, out var entry))
            {
                return false;
            }

            entry.Database.Dispose();
            return true;
        }
    }

    internal static void Release(string databasePath, SharpCoreDbIndexDatabase database)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(databasePath, out var entry) || !ReferenceEquals(entry.Database, database))
            {
                // The entry was invalidated out from under this lease; closing our own handle twice would be a
                // bug in the cache, so the handle is left to the disposal that already happened.
                return;
            }

            entry.Leases--;
            if (entry.Leases > 0)
            {
                return;
            }

            Entries.Remove(databasePath);
            database.Dispose();
        }
    }

    private sealed class Entry(SharpCoreDbIndexDatabase database)
    {
        public SharpCoreDbIndexDatabase Database { get; } = database;

        public int Leases { get; set; } = 1;
    }
}
