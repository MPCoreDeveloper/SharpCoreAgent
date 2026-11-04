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
/// A build therefore names every id it writes, from <c>MAX(id)</c> at the moment it first writes to a table. The
/// engine's counter is never consulted, so rows of a later build cannot land on rows of an earlier one, and a
/// reader resolves every passage to exactly one file.
/// </para>
/// <para>
/// That read is a scan, and the passage table is the one a large index pays for: <c>SELECT MAX(id) FROM chunks</c>
/// cost 2 598 ms over 14 434 rows on this repository's index, and a build that asked for every table up front spent
/// about 3.6 s of a 5.5 s run that changed nothing (spec 002, D27). A table is therefore read when it is first asked
/// for an id instead of when the writer opens, which is where the work already is: a refresh that finds every file
/// unchanged writes no row and now reads no table, a prune only deletes, and a build that adds a file reads the
/// tables that file touches. The read still happens before this build's first write to that table, so the ids
/// still cannot land on a stored row.
/// </para>
/// <para>
/// Reading lazily is not on its own enough to make a no-op pass cheap: the <c>meta</c> batch that follows a pass
/// like that costs 5 107 ms when nothing read the pages before it and 2 537 ms when something did (D27). It is what
/// lets the rest of the pass skip the work — no writes, no reads — and that is the half of the pass these ids are
/// responsible for.
/// </para>
/// </remarks>
internal sealed class IndexIdSequence
{
    private readonly IDatabase _database;
    private readonly Dictionary<IndexTable, long> _last = [];

    private IndexIdSequence(IDatabase database) => _database = database;

    /// <summary>
    /// Prepares to hand out ids above the ones already stored.
    /// </summary>
    /// <param name="database">The index the build is about to write.</param>
    /// <returns>
    /// A sequence that reads a table the first time that table is asked for an id; <c>meta</c> is not one of
    /// them, because it is keyed by its own column.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="database"/> is null.</exception>
    public static IndexIdSequence For(IDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        return new IndexIdSequence(database);
    }

    /// <summary>
    /// Names the next row of one table.
    /// </summary>
    /// <param name="table">The table the row goes into.</param>
    /// <returns>An id no row in that table holds.</returns>
    public long Next(IndexTable table)
    {
        // Once per table and build: the first id a table hands out is what makes the sequence read it, and every
        // later id continues from the counter this method keeps.
        if (!_last.TryGetValue(table, out var last))
        {
            last = IndexSql.MaxId(_database, table);
        }

        var next = last + 1;
        _last[table] = next;
        return next;
    }
}
