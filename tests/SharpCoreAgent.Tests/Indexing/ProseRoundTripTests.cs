using SharpCoreAgent.Domain;
using SharpCoreAgent.Indexing;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The prose an index has to store verbatim, and the path it has to find again afterwards.
/// </summary>
/// <remarks>
/// A bound value reaches the engine's parser as statement <em>text</em>: <c>ExecuteSQL(sql, @parameters)</c>
/// renders every value back into the statement before parsing it, so the formatter and the literal scanner have
/// to agree about escaping. In <c>2.1.1</c> they did not — a <c>TEXT</c> holding an apostrophe, a doubled quote or
/// a backslash was re-scanned as SQL, so the value landed in the neighbouring column or the statement failed with
/// "Mismatched parentheses in VALUES clause" (SharpCoreDB known issue 7, fixed in <c>2.1.2</c> by
/// <c>Services/SqlLiteralScanner.cs</c>). The cases below are the shapes that issue lists, plus the one route this
/// index takes on every first build — <c>SELECT id FROM files WHERE path = @path</c> — with a workspace directory
/// whose name holds an apostrophe. Six of the eight fail on <c>2.1.1</c>: the ones that need a quote, a doubled
/// quote or a backslash, plus the indented passage, because that version stripped the surrounding whitespace off a
/// bound <c>TEXT</c>. Parentheses and commas alone were always safe, which is how the defect stayed hidden. All
/// eight pass on <c>2.1.2</c>, and that is what makes the pinned version a floor rather than a preference.
/// </remarks>
public sealed class ProseRoundTripTests
{
    private const string ReportSource = """
        namespace Demo.Ops;

        public static class Report
        {
            public static string Title() => "it's done";
        }
        """;

    private const string PlainSource = """
        namespace Demo.Ops;

        public static class Plain
        {
            public static int One() => 1;
        }
        """;

    [Theory]
    [InlineData("// it's fine, really")]
    [InlineData("// a stray \\' apostrophe, then a comma")]
    [InlineData("// ends with a backslash \\")]
    [InlineData("// the mask (see below, the reader) wins, and then it records iterations)")]
    [InlineData("// 'quoted', 'doubled''quotes', 1, 2, 3")]
    [InlineData("// C:\\Users\\O'Brien\\src\\it's.cs")]
    [InlineData("  // an indented passage, with trailing spaces  ")]
    public void ProseSurvivesABoundStatement(string prose)
    {
        using var sandbox = ToolTestWorkspace.Create();
        var paths = IndexPaths.For(sandbox.Workspace);
        IndexTestDatabase.Create(paths);

        using var lease = IndexHandleCache.Acquire(paths, encryptionPassword: null, createIfMissing: false);
        var database = lease.Database.Database;
        var sha256 = new string('c', 64);

        IndexSql.Execute(
            database,
            "INSERT INTO chunks (file_id, start_line, end_line, kind, text, tokens, sha256) VALUES (1, 1, 1, 'chunk', @text, @tokens, @sha256)",
            IndexSql.Parameters(("@text", prose), ("@tokens", "tokens"), ("@sha256", sha256)));

        var row = Assert.Single(IndexSql.Query(database, "SELECT text, tokens, sha256 FROM chunks"));

        // The value, not a fragment of it, and the columns after it holding what they were given rather than a
        // tail that shifted left: a shifted row is the silent half of the defect.
        Assert.Equal(prose, IndexSql.AsText(row["text"]));
        Assert.Equal("tokens", IndexSql.AsText(row["tokens"]));
        Assert.Equal(sha256, IndexSql.AsText(row["sha256"]));
    }

    [Fact]
    public async Task APathWithAnApostropheIsIndexedAndFoundAgain()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("O'Brien/Report.cs", ReportSource);
        sandbox.Write("src/Plain.cs", PlainSource);

        await using var maintainer = SharpCoreDbIndexMaintainer.For(sandbox.Workspace);
        var report = await maintainer.EnsureFreshAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Added);
        Assert.False(report.Incomplete);
        Assert.Equal(2, report.Status.Files);
        Assert.Equal(["O'Brien/Report.cs", "src/Plain.cs"], ReadPaths(sandbox.Workspace));
    }

    private static List<string> ReadPaths(Workspace workspace)
    {
        using var lease = IndexHandleCache.Acquire(
            IndexPaths.For(workspace),
            encryptionPassword: null,
            createIfMissing: false);

        return IndexSql.Query(lease.Database.Database, "SELECT path FROM files")
            .Select(row => IndexSql.AsText(row.GetValueOrDefault("path")))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }
}
