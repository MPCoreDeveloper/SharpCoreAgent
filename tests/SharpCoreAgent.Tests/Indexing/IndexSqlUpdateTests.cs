using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The row-addressed write: what a batch does with it, and what it leaves alone.
/// </summary>
/// <remarks>
/// <para>
/// <c>CommunityAssigner</c> writes both community columns this way because a statement per declaration cost 11 ms on a real
/// index, and the pass needed the store's batch to behave exactly as it does for the statements it replaced: one commit at
/// the end, and nothing at all when the batch is abandoned (spec 028, D3).
/// </para>
/// <para>
/// The third case is the one that catches what nothing else can: a row-addressed write names columns, so a store that
/// replaced the row instead of updating it would blank the extracted facts — the declaration's name, its signature, its
/// documentation — and every consumer of the index would still find the columns it asked for.
/// </para>
/// </remarks>
public sealed class IndexSqlUpdateTests
{
    [Fact]
    public void ACommittedBatchStoresTheRow()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var paths = IndexPaths.For(sandbox.Workspace);
        IndexTestDatabase.Create(paths);
        InsertSymbol(paths, id: 1, key: "Demo.One.Run", name: "Run", community: "c0");

        using (var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false))
        {
            lease.Database.Database.BeginBatchUpdate();
            Assert.True(IndexSql.Update(
                lease.Database.Database,
                IndexTable.Symbols,
                1L,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["community"] = "c7",
                    ["centrality"] = 0.25,
                }));
            lease.Database.Database.EndBatchUpdate();
        }

        using var read = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false);
        var row = ReadSymbol(read.Database.Database, 1);
        Assert.Equal("c7", IndexSql.AsText(row.GetValueOrDefault("community")));
        Assert.Equal(
            0.25,
            Convert.ToDouble(row.GetValueOrDefault("centrality"), System.Globalization.CultureInfo.InvariantCulture),
            6);
    }

    [Fact]
    public void ACancelledBatchLeavesThePreviousValue()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var paths = IndexPaths.For(sandbox.Workspace);
        IndexTestDatabase.Create(paths);
        InsertSymbol(paths, id: 1, key: "Demo.One.Run", name: "Run", community: "c0");

        using (var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false))
        {
            lease.Database.Database.BeginBatchUpdate();
            IndexSql.Update(
                lease.Database.Database,
                IndexTable.Symbols,
                1L,
                new Dictionary<string, object>(StringComparer.Ordinal) { ["community"] = "c7" });
            lease.Database.Database.CancelBatchUpdate();
        }

        using var read = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false);
        var row = ReadSymbol(read.Database.Database, 1);
        Assert.Equal("c0", IndexSql.AsText(row.GetValueOrDefault("community")));
        Assert.Equal(0, IndexSql.AsLong(row.GetValueOrDefault("centrality")));
    }

    [Fact]
    public void OnlyTheNamedColumnsChange()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var paths = IndexPaths.For(sandbox.Workspace);
        IndexTestDatabase.Create(paths);
        InsertSymbol(paths, id: 1, key: "Demo.One.Run", name: "Run", community: "c0");

        using (var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false))
        {
            lease.Database.Database.BeginBatchUpdate();
            IndexSql.Update(
                lease.Database.Database,
                IndexTable.Symbols,
                1L,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["community"] = "c7",
                    ["centrality"] = 0.5,
                });
            lease.Database.Database.EndBatchUpdate();
        }

        using var read = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false);
        var row = ReadSymbol(read.Database.Database, 1);
        Assert.Equal("Demo.One.Run", IndexSql.AsText(row.GetValueOrDefault("key")));
        Assert.Equal("Run", IndexSql.AsText(row.GetValueOrDefault("name")));
        Assert.Equal("Demo.One.Run", IndexSql.AsText(row.GetValueOrDefault("qualified_name")));
        Assert.Equal("method", IndexSql.AsText(row.GetValueOrDefault("kind")));
        Assert.Equal("the documented one", IndexSql.AsText(row.GetValueOrDefault("doc")));
        Assert.Equal("public static int Run()", IndexSql.AsText(row.GetValueOrDefault("signature")));
        Assert.Equal(11, IndexSql.AsLong(row.GetValueOrDefault("start_line")));
        Assert.Equal(19, IndexSql.AsLong(row.GetValueOrDefault("end_line")));
    }

    /// <summary>Inserts one declaration wide enough that a row replace would be visible.</summary>
    /// <param name="paths">The index to write to.</param>
    /// <param name="id">The row's primary key.</param>
    /// <param name="key">The symbol key.</param>
    /// <param name="name">Its short name.</param>
    /// <param name="community">The label it starts with.</param>
    private static void InsertSymbol(IndexPaths paths, long id, string key, string name, string community)
    {
        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false);
        IndexSql.Insert(
            lease.Database.Database,
            IndexTable.Symbols,
            IndexSql.Row(
                ("id", id),
                ("file_id", 1L),
                ("key", key),
                ("name", name),
                ("qualified_name", key),
                ("kind", "method"),
                ("start_line", 11L),
                ("end_line", 19L),
                ("signature", "public static int Run()"),
                ("doc", "the documented one"),
                ("community", community)));
    }

    /// <summary>Reads one declaration back, with every column it has.</summary>
    /// <param name="database">The index to read.</param>
    /// <param name="id">The row's primary key.</param>
    /// <returns>Its columns.</returns>
    private static Dictionary<string, object?> ReadSymbol(SharpCoreDB.Interfaces.IDatabase database, long id)
    {
        var rows = IndexSql.Query(
            database,
            "SELECT * FROM symbols WHERE id = " + id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Single(rows);
        return rows[0];
    }
}
