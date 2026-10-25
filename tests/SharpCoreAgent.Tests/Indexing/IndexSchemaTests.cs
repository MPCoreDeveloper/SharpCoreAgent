using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The schema is the one thing every later milestone assumes exists, and it is written as SQL that a
/// pre-release engine has to accept, so these tests run the real DDL against a real database in a throwaway
/// workspace rather than asserting that a string contains a table name.
/// </summary>
/// <remarks>
/// A schema test that mocks the engine proves nothing here: the risk this milestone carries is precisely that
/// <c>CREATE TABLE</c>, <c>INTEGER PRIMARY KEY AUTO</c>, <c>CHECK</c>, <c>COLLATE NOCASE</c> and <c>VECTOR(n)</c>
/// either parse or do not.
/// </remarks>
public sealed class IndexSchemaTests
{
    [Fact]
    public void CreatesAllSevenTables()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var paths = IndexPaths.For(sandbox.Workspace);

        using var database = SharpCoreDbIndexDatabase.OpenOrCreate(paths, encryptionPassword: null);
        IndexSchema.EnsureCreated(database.Database, paths);

        foreach (var table in IndexSchema.Tables)
        {
            // A select against a missing table throws, which is the assertion; the count is a bonus.
            var rows = database.Database.ExecuteQuery($"SELECT * FROM {table}");
            Assert.NotNull(rows);
        }

        Assert.Equal(
            ["meta", "files", "chunks", "symbols", "edges", "node_embeddings", "docs"],
            IndexSchema.Tables);
    }

    [Fact]
    public void SeedsEveryMetaKey()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var paths = IndexPaths.For(sandbox.Workspace);

        using var database = SharpCoreDbIndexDatabase.OpenOrCreate(paths, encryptionPassword: null);
        IndexSchema.EnsureCreated(database.Database, paths);

        var meta = IndexSchema.ReadMeta(database.Database);

        Assert.Equal(15, meta.Count);
        Assert.Equal("1", meta["schema_version"]);
        Assert.Equal("1", meta["parser_version"]);
        Assert.Equal("1", meta["analyzer_contract"]);
        Assert.Equal("lean", meta["tier"]);
        Assert.Equal("hashed-local/512", meta["embedder"]);
        Assert.Equal("512", meta["dimensions"]);
        Assert.Equal("int8", meta["quantization"]);
        Assert.Equal(sandbox.Root.Replace('\\', '/'), meta["workspace_root"]);
        Assert.Equal(string.Empty, meta["build_id"]);
        Assert.Equal(string.Empty, meta["build_utc"]);
        Assert.Equal(string.Empty, meta["build_state"]);
        Assert.Equal("0", meta["file_count"]);
        Assert.Equal("0", meta["chunk_count"]);
        Assert.Equal("0", meta["symbol_count"]);
        Assert.Equal("0", meta["edge_count"]);
    }

    [Fact]
    public void RunningTwiceChangesNothing()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var paths = IndexPaths.For(sandbox.Workspace);

        using var database = SharpCoreDbIndexDatabase.OpenOrCreate(paths, encryptionPassword: null);
        IndexSchema.EnsureCreated(database.Database, paths);
        IndexSchema.EnsureCreated(database.Database, paths);

        var meta = IndexSchema.ReadMeta(database.Database);

        // Counting rows in C# rather than through COUNT(*) AS n: the engine does not carry a column alias
        // through ExecuteQuery, and a duplicate seed would show as a count above fifteen anyway.
        Assert.Equal(15, meta.Count);
    }

    [Fact]
    public void DimensionsMatchTheVectorDeclarations()
    {
        Assert.Equal(512, IndexSchema.Dimensions);
        Assert.Equal(
            2,
            IndexSchema.Statements.Count(statement => statement.Contains($"VECTOR({IndexSchema.Dimensions})", StringComparison.Ordinal)));
    }

    [Fact]
    public void EdgesCarryTheGraphColumnNames()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var paths = IndexPaths.For(sandbox.Workspace);

        using var database = SharpCoreDbIndexDatabase.OpenOrCreate(paths, encryptionPassword: null);
        IndexSchema.EnsureCreated(database.Database, paths);

        // GraphLoader and GraphTraversalEngine read these three names; the insert is what proves they exist
        // with the types the graph leg expects.
        database.Database.ExecuteSQL("INSERT INTO edges (source, target, rel, weight) VALUES (1, 2, 'calls', 1.0)");

        var rows = database.Database.ExecuteQuery("SELECT source, target, weight FROM edges");

        Assert.Single(rows);
        Assert.Equal("1", rows[0]["source"].ToString());
        Assert.Equal("2", rows[0]["target"].ToString());
        Assert.Equal("1", rows[0]["weight"].ToString());
    }
}
