using System.Globalization;
using System.Text.Json;
using SharpCoreAgent.Cli;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The <c>index</c> command end to end: what it prints, what it writes and which exit code a script branches on.
/// </summary>
/// <remarks>
/// Each test drives the command the way <c>Program</c> does — parse, execute, read the report — against a throwaway
/// workspace, because the interesting failures here are all about the file system: an index that does not exist yet, a
/// file that changed behind its back, and a directory that cannot be created. The report goes to a buffer rather than
/// to <see cref="Console"/>, so the assertions read what a pipeline would.
/// </remarks>
public sealed class IndexCommandTests
{
    [Fact]
    public async Task StatusOnAWorkspaceWithoutAnIndexSaysSoAndWritesNothing()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("src/Circle.cs", "internal sealed class Circle { }");

        var (exitCode, output, error) = await RunAsync(sandbox, "index", "--status");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(error);
        Assert.Contains("state       missing", output, StringComparison.Ordinal);
        Assert.Contains("run `sharpcoreagent index` to build it", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(sandbox.Root, ".sharpcoreagent")));
    }

    [Fact]
    public async Task ABareIndexBuildsAndReportsWhatItWrote()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        var (exitCode, output, _) = await RunAsync(sandbox, "index");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("indexing ", output, StringComparison.Ordinal);
        Assert.Contains("2 added", output, StringComparison.Ordinal);
        Assert.Contains("state       fresh", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusJsonIsOneLineWithTheDocumentedKeys()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await RunAsync(sandbox, "index");

        var (exitCode, output, _) = await RunAsync(sandbox, "index", "--status", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        Assert.Equal("index", root.GetProperty("command").GetString());
        Assert.Equal("status", root.GetProperty("action").GetString());
        Assert.Equal("fresh", root.GetProperty("state").GetString());
        Assert.Equal("lean", root.GetProperty("tier").GetString());
        Assert.Equal("hashed-local/512", root.GetProperty("embedder").GetString());
        Assert.Equal(512, root.GetProperty("dimensions").GetInt32());
        Assert.Equal(2, root.GetProperty("files").GetInt32());
        Assert.Equal(0, root.GetProperty("stale").GetInt32());
        Assert.False(root.GetProperty("incomplete").GetBoolean());
        Assert.True(root.GetProperty("passages").GetInt32() > 0);

        string[] keys =
        [
            "files", "orphans", "skipped", "stale", "symbols", "edges", "passages", "sizeBytes",
            "vectorSearch", "builtUtc", "notes",
        ];

        Assert.All(keys, key => Assert.True(root.TryGetProperty(key, out _), $"missing key '{key}'"));
    }

    [Fact]
    public async Task BuildJsonCarriesTheCountsAScriptBranchesOn()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        var (exitCode, output, _) = await RunAsync(sandbox, "index", "--json");

        Assert.Equal(ExitCodes.Success, exitCode);

        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;

        Assert.Equal("build", root.GetProperty("action").GetString());
        Assert.Equal(2, root.GetProperty("added").GetInt32());
        Assert.Equal(0, root.GetProperty("changed").GetInt32());
        Assert.Equal(0, root.GetProperty("removed").GetInt32());
        Assert.Equal(0, root.GetProperty("unchanged").GetInt32());
        Assert.False(root.GetProperty("incomplete").GetBoolean());
        Assert.Equal(2, root.GetProperty("files").GetInt32());
    }

    [Fact]
    public async Task VerifyOnADriftedIndexExitsOneAndRebuildRepairsIt()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await RunAsync(sandbox, "index");

        // Edited behind the index's back: the hash in the row no longer matches the file.
        await File.AppendAllTextAsync(
            sandbox.FullPath("src/Circle.cs"),
            "// drift\n",
            TestContext.Current.CancellationToken);

        var (drifted, driftOutput, _) = await RunAsync(sandbox, "index", "--verify");

        Assert.Equal(ExitCodes.Failed, drifted);
        Assert.Contains("result      drift", driftOutput, StringComparison.Ordinal);
        Assert.Contains("--rebuild", driftOutput, StringComparison.Ordinal);

        var (rebuilt, _, _) = await RunAsync(sandbox, "index", "--rebuild");
        var (repaired, repairedOutput, _) = await RunAsync(sandbox, "index", "--verify");

        Assert.Equal(ExitCodes.Success, rebuilt);
        Assert.Equal(ExitCodes.Success, repaired);
        Assert.Contains("result      fresh", repairedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PruneDropsTheRowsOfAFileThatWasDeleted()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);
        await RunAsync(sandbox, "index");

        File.Delete(sandbox.FullPath("src/Square.cs"));

        var (exitCode, output, _) = await RunAsync(sandbox, "index", "--prune");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("1 removed", output, StringComparison.Ordinal);

        // The report above is prose; the count a script reads comes from --status.
        var (_, statusJson, _) = await RunAsync(sandbox, "index", "--status", "--json");

        using var document = JsonDocument.Parse(statusJson);
        Assert.Equal(1, document.RootElement.GetProperty("files").GetInt32());
    }

    [Fact]
    public async Task AnIndexDirectoryThatCannotBeCreatedExitsSix()
    {
        using var sandbox = ToolTestWorkspace.Create();
        WriteCorpus(sandbox);

        // A file where the directory must go: every later attempt to create .sharpcoreagent/index fails.
        await File.WriteAllTextAsync(
            Path.Combine(sandbox.Root, ".sharpcoreagent"),
            "not a directory",
            TestContext.Current.CancellationToken);

        var (exitCode, output, error) = await RunAsync(sandbox, "index");

        Assert.Equal(ExitCodes.IndexUnavailable, exitCode);
        Assert.Empty(output);
        Assert.StartsWith("error: ", error, StringComparison.Ordinal);
    }

    private static void WriteCorpus(ToolTestWorkspace sandbox)
    {
        sandbox.Write(
            "src/Circle.cs",
            """
            namespace Demo.Geometry;

            internal sealed class Circle
            {
                internal double Circumference() => 6.28;
            }
            """);

        sandbox.Write(
            "src/Square.cs",
            """
            namespace Demo.Geometry;

            internal sealed class Square
            {
                internal double Side { get; init; } = 2;
            }
            """);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        ToolTestWorkspace sandbox,
        params string[] arguments)
    {
        string[] line = [.. arguments, "--workspace", sandbox.Root];
        var options = CliOptions.Parse(line, _ => null);
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await IndexCommand.ExecuteAsync(
            options,
            output,
            error,
            TestContext.Current.CancellationToken);

        return (exitCode, output.ToString(), error.ToString());
    }
}