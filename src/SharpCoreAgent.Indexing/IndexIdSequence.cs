using SharpCoreDB.Interfaces;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// The ids one build writes, named by the writer instead of by the engine.
/// </summary>
/// <remarks>
/// The <c>INTEGER PRIMARY KEY AUTO</c> column does not continue from the rows already stored: a connection
/// opened on an index that an earlier build filled starts its counter at one again. The engine accepts the
/// repeated values — the column is not unique in practice — so a second build stores its files under ids the
/// first build's files already hold, and every <c>file_id</c> a passage carries then names several files.
/// Nothing crashes at that point; a query simply answers with a passage from whichever file shares the id.
/// Two such builds on a tree of a few hundred files make the collision total, which is where
/// <see cref="Graph.CommunityAssigner"/> met it: twelve thousand declarations under two thousand ids, and the
/// adjacency array sized by the second number while the positions came from the first.
/// <para>
/// A build therefore names every id it writes, from <c>MAX(id)</c> at the moment the writer opens. The engine's
/// counter is never consulted, so rows of a later build cannot land on rows of an earlier one, and a reader
/// resolves every passage to exactly one file.
/// </para>
/// </remarks>
internal sealed class IndexIdSequence
{
    /// <summary>The tables whose ids this build names; <c>meta</c> is keyed by its own column.</summary>
    private static readonly IndexTable[] AllocatedTables =
    [
        IndexTable.Files,
        IndexTable.Chunks,
        IndexTable.Symbols,
        IndexTable.Edges,
        IndexTable.Docs,
        IndexTable.NodeEmbeddings,
    ];

    private readonly Dictionary<IndexTable, long> _last;

    private IndexIdSequence(Dictionary<IndexTable, long> last) => _last = last;

    /// <summary>
    /// Reads where each table's numbering stands.
    /// </summary>
    /// <param name="database">The index the build is about to write.</param>
    /// <returns>A sequence that hands out ids above every id already stored.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    public static IndexIdSequence Load(IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        var last = new Dictionary<IndexTable, long>(AllocatedTables.Length);
        foreach (var table in AllocatedTables)
        {
            last[table] = IndexSql.MaxId(database, table);
        }

        return new IndexIdSequence(last);
    }

    /// <summary>
    /// Names the next row of one table.
    /// </summary>
    /// <param name="table">The table the row goes into.</param>
    /// <returns>An id no row in that table holds.</returns>
    public long Next(IndexTable table)
    {
        var next = _last[table] + 1;
        _last[table] = next;
        return next;
    }
}
