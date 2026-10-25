namespace SharpCoreAgent.Indexing;

/// <summary>
/// One caller's share of a cached index handle.
/// </summary>
/// <remarks>
/// Disposing the lease twice is harmless, which matters because a lease lives inside a class that also has a
/// final shutdown path: <see cref="IndexHandleCache"/> counts references, and an unbalanced decrement would
/// close the database under somebody else.
/// </remarks>
internal sealed class IndexHandleLease : IDisposable
{
    private int _released;

    internal IndexHandleLease(SharpCoreDbIndexDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        Database = database;
    }

    /// <summary>Gets the shared open handle.</summary>
    public SharpCoreDbIndexDatabase Database { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            IndexHandleCache.Release(Database.DatabasePath, Database);
        }
    }
}
