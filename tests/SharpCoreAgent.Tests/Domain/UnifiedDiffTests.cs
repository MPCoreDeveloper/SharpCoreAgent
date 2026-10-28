using SharpCoreAgent.Domain.Text;
using Xunit;

namespace SharpCoreAgent.Tests.Domain;

/// <summary>
/// The diff is what the approval prompt shows, so its headers and hunk arithmetic are pinned exactly:
/// an off-by-one line number sends the developer to the wrong place in the file.
/// </summary>
public sealed class UnifiedDiffTests
{
    [Fact]
    public void IdenticalTextsProduceNoDiff()
    {
        var diff = UnifiedDiff.Create("src/a.cs", "one\ntwo\n", "one\ntwo\n");

        Assert.Equal(string.Empty, diff);
    }

    [Fact]
    public void SingleLineChangeRendersOneHunkWithContext()
    {
        var diff = UnifiedDiff.Create("src/a.cs", "a\nb\nc\nd\n", "a\nb\nX\nd\n");

        Assert.Equal(
            "--- a/src/a.cs\n+++ b/src/a.cs\n@@ -3,1 +3,1 @@\n a\n b\n-c\n+X\n d\n",
            diff);
    }

    [Fact]
    public void InsertionAtTheTopUsesTheZeroLengthRange()
    {
        var diff = UnifiedDiff.Create("notes.txt", "a\n", "new\na\n");

        Assert.Contains("@@ -0,0 +1,1 @@", diff, StringComparison.Ordinal);
        Assert.Contains("+new\n", diff, StringComparison.Ordinal);
        Assert.Contains(" a\n", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void CrLfAndLfAreComparedByLine()
    {
        var diff = UnifiedDiff.Create("a.txt", "one\r\ntwo\r\n", "one\ntwo\n");

        Assert.Equal(string.Empty, diff);
    }

    [Fact]
    public void LargeChangesAreTruncatedWithACount()
    {
        var before = string.Join('\n', Enumerable.Range(0, 40).Select(index => $"old {index}")) + "\n";
        var after = string.Join('\n', Enumerable.Range(0, 40).Select(index => $"new {index}")) + "\n";

        var diff = UnifiedDiff.Create("big.txt", before, after, contextLines: 0, maxLines: 6);

        Assert.Contains("more line(s)", diff, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxLinesMustLeaveRoomForHeaders()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UnifiedDiff.Create("a.txt", "a", "b", maxLines: 3));
    }
}
