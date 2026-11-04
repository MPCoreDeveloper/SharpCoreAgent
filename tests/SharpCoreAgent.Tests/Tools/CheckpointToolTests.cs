using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Checkpoints;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>The three file-mutating tools capture before they change anything.</summary>
public sealed class CheckpointToolTests
{
    [Fact]
    public async Task WriteFileRestoresThePreviousContent()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("notes.txt", "original");
        var journal = new TurnCheckpointStore();

        var turn = journal.BeginTurn();
        var result = await new WriteFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("write_file", """{"path":"notes.txt","content":"changed"}"""),
            sandbox.Context with { Checkpoint = turn },
            CancellationToken.None);
        turn.Dispose();

        Assert.False(result.IsError, result.Content);
        Assert.Equal("changed", Text(sandbox, "notes.txt"));
        Assert.True(journal.UndoLast().Succeeded);
        Assert.Equal("original", Text(sandbox, "notes.txt"));
    }

    [Fact]
    public async Task ReplaceInFileRestoresTheOriginalFragment()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "before\n");
        var journal = new TurnCheckpointStore();

        var turn = journal.BeginTurn();
        var result = await new ReplaceInFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("replace_in_file", """{"path":"a.txt","find":"before","replace":"after"}"""),
            sandbox.Context with { Checkpoint = turn },
            CancellationToken.None);
        turn.Dispose();

        Assert.False(result.IsError, result.Content);
        Assert.Equal("after\n", Text(sandbox, "a.txt"));
        Assert.True(journal.UndoLast().Succeeded);
        Assert.Equal("before\n", Text(sandbox, "a.txt"));
    }

    [Fact]
    public async Task DeleteFileRestoresTheDeletedFile()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "original");
        var journal = new TurnCheckpointStore();

        var turn = journal.BeginTurn();
        var result = await new DeleteFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("delete_file", """{"path":"a.txt"}"""),
            sandbox.Context with { Checkpoint = turn },
            CancellationToken.None);
        turn.Dispose();

        Assert.False(result.IsError, result.Content);
        Assert.False(File.Exists(sandbox.FullPath("a.txt")));
        Assert.True(journal.UndoLast().Succeeded);
        Assert.Equal("original", Text(sandbox, "a.txt"));
    }

    [Fact]
    public async Task WriteFileCanCreateInANewDirectoryAndUndoRemovesTheFile()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var journal = new TurnCheckpointStore();

        var turn = journal.BeginTurn();
        var result = await new WriteFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("write_file", """{"path":"new/notes.txt","content":"created"}"""),
            sandbox.Context with { Checkpoint = turn },
            CancellationToken.None);
        turn.Dispose();

        Assert.False(result.IsError, result.Content);
        Assert.True(File.Exists(sandbox.FullPath("new/notes.txt")));
        var undo = journal.UndoLast();
        Assert.True(undo.Succeeded);
        Assert.Equal(["new/notes.txt"], undo.Removed);
        Assert.False(File.Exists(sandbox.FullPath("new/notes.txt")));
    }

    [Fact]
    public async Task ACaptureFailureRefusesTheWriteAndLeavesTheFileAlone()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "original");

        var result = await new WriteFileTool().ExecuteAsync(
            ToolTestWorkspace.Call("write_file", """{"path":"a.txt","content":"changed"}"""),
            sandbox.Context with { Checkpoint = new RefusingCheckpoint() },
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("could not checkpoint", result.Content, StringComparison.Ordinal);
        Assert.Equal("original", Text(sandbox, "a.txt"));
    }

    private sealed class RefusingCheckpoint : ITurnCheckpoint
    {
        public ValueTask<string?> CaptureAsync(
            string relativePath,
            string fullPath,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>("could not checkpoint this file");

        public void Dispose()
        {
        }
    }

    private static string Text(ToolTestWorkspace sandbox, string path) =>
        File.ReadAllText(sandbox.FullPath(path));
}
