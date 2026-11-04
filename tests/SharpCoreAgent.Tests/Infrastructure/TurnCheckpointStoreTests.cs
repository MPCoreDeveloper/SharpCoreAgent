using SharpCoreAgent.Infrastructure.Checkpoints;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>One turn's file states, and the rules <c>/undo</c> depends on.</summary>
public sealed class TurnCheckpointStoreTests
{
    [Fact]
    public async Task UndoRestoresExistingBytesAndRemovesCreatedFiles()
    {
        using var sandbox = ToolTestWorkspace.Create();
        byte[] original = [0xEF, 0xBB, 0xBF, (byte)'o', (byte)'l', (byte)'d'];
        sandbox.WriteBytes("existing.txt", original);
        var journal = new TurnCheckpointStore();

        var turn = journal.BeginTurn();
        Assert.Null(await turn.CaptureAsync("existing.txt", sandbox.FullPath("existing.txt"), CancellationToken.None));
        Assert.Null(await turn.CaptureAsync("created.txt", sandbox.FullPath("created.txt"), CancellationToken.None));
        sandbox.Write("existing.txt", "new");
        sandbox.Write("created.txt", "created");
        turn.Dispose();

        var undo = journal.UndoLast();

        Assert.True(undo.HadCheckpoint);
        Assert.True(undo.Succeeded);
        Assert.Equal(["existing.txt"], undo.Restored);
        Assert.Equal(["created.txt"], undo.Removed);
        Assert.Equal(original, Bytes(sandbox, "existing.txt"));
        Assert.False(File.Exists(sandbox.FullPath("created.txt")));
    }

    [Fact]
    public async Task TheFirstCaptureOfAFileWins()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "original");
        var journal = new TurnCheckpointStore();

        var turn = journal.BeginTurn();
        await turn.CaptureAsync("a.txt", sandbox.FullPath("a.txt"), CancellationToken.None);
        sandbox.Write("a.txt", "second");
        Assert.Null(await turn.CaptureAsync("a.txt", sandbox.FullPath("a.txt"), CancellationToken.None));
        sandbox.Write("a.txt", "third");
        turn.Dispose();

        var undo = journal.UndoLast();

        Assert.True(undo.Succeeded);
        Assert.Equal("original", Text(sandbox, "a.txt"));
    }

    [Fact]
    public async Task ADeletedFileIsRestored()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "original");
        var journal = new TurnCheckpointStore();

        var turn = journal.BeginTurn();
        await turn.CaptureAsync("a.txt", sandbox.FullPath("a.txt"), CancellationToken.None);
        File.Delete(sandbox.FullPath("a.txt"));
        turn.Dispose();

        var undo = journal.UndoLast();

        Assert.True(undo.Succeeded);
        Assert.Equal("original", Text(sandbox, "a.txt"));
    }

    [Fact]
    public async Task TurnsUndoInLifoOrder()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "original");
        var journal = new TurnCheckpointStore();

        var first = journal.BeginTurn();
        await first.CaptureAsync("a.txt", sandbox.FullPath("a.txt"), CancellationToken.None);
        sandbox.Write("a.txt", "first");
        first.Dispose();

        var second = journal.BeginTurn();
        await second.CaptureAsync("a.txt", sandbox.FullPath("a.txt"), CancellationToken.None);
        sandbox.Write("a.txt", "second");
        second.Dispose();

        var undoSecond = journal.UndoLast();
        Assert.Equal(["a.txt"], undoSecond.Restored);
        Assert.Equal("first", Text(sandbox, "a.txt"));

        var undoFirst = journal.UndoLast();
        Assert.Equal(["a.txt"], undoFirst.Restored);
        Assert.Equal("original", Text(sandbox, "a.txt"));
        Assert.False(journal.UndoLast().HadCheckpoint);
    }

    [Fact]
    public async Task AReadOnlyTurnDoesNotHideThePreviousCheckpoint()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "original");
        var journal = new TurnCheckpointStore();

        var edit = journal.BeginTurn();
        await edit.CaptureAsync("a.txt", sandbox.FullPath("a.txt"), CancellationToken.None);
        sandbox.Write("a.txt", "edited");
        edit.Dispose();

        journal.BeginTurn().Dispose();

        Assert.True(journal.UndoLast().Succeeded);
        Assert.Equal("original", Text(sandbox, "a.txt"));
    }

    [Fact]
    public async Task ANetNoOpTurnIsNotRecorded()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "original");
        var journal = new TurnCheckpointStore();

        var turn = journal.BeginTurn();
        await turn.CaptureAsync("a.txt", sandbox.FullPath("a.txt"), CancellationToken.None);
        turn.Dispose();

        Assert.False(journal.UndoLast().HadCheckpoint);
    }

    [Fact]
    public async Task ANestedTurnMergesIntoItsParent()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "original");
        var journal = new TurnCheckpointStore();

        var parent = journal.BeginTurn();
        var child = journal.BeginTurn();
        await child.CaptureAsync("a.txt", sandbox.FullPath("a.txt"), CancellationToken.None);
        sandbox.Write("a.txt", "child");
        child.Dispose();
        parent.Dispose();

        Assert.True(journal.UndoLast().Succeeded);
        Assert.Equal("original", Text(sandbox, "a.txt"));
        Assert.False(journal.UndoLast().HadCheckpoint);
    }

    [Fact]
    public async Task AFileChangedAfterTheTurnIsLeftAloneAndTheCheckpointIsKept()
    {
        using var sandbox = ToolTestWorkspace.Create();
        sandbox.Write("a.txt", "original");
        var journal = new TurnCheckpointStore();

        var turn = journal.BeginTurn();
        await turn.CaptureAsync("a.txt", sandbox.FullPath("a.txt"), CancellationToken.None);
        sandbox.Write("a.txt", "agent");
        turn.Dispose();
        sandbox.Write("a.txt", "developer");

        var undo = journal.UndoLast();

        Assert.True(undo.HadCheckpoint);
        Assert.False(undo.Succeeded);
        Assert.Equal("developer", Text(sandbox, "a.txt"));
        Assert.Contains("changed after the turn ended", Assert.Single(undo.Failures).Reason, StringComparison.Ordinal);

        // Putting the file back to the state the turn left makes the checkpoint applicable again.
        sandbox.Write("a.txt", "agent");
        Assert.True(journal.UndoLast().Succeeded);
        Assert.Equal("original", Text(sandbox, "a.txt"));
    }

    private static string Text(ToolTestWorkspace sandbox, string path) =>
        File.ReadAllText(sandbox.FullPath(path));

    private static byte[] Bytes(ToolTestWorkspace sandbox, string path) =>
        File.ReadAllBytes(sandbox.FullPath(path));
}
