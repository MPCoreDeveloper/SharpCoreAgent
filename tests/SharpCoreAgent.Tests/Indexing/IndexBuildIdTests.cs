using System.Text;
using SharpCoreAgent.Indexing;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// The build id is the token a reader compares to know whether the rows it cached belong to the build on disk,
/// so it has to be unique per run, sortable by time and stable in shape.
/// </summary>
public sealed class IndexBuildIdTests
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    [Fact]
    public void HasTheUlidShape()
    {
        var id = IndexBuildId.NewId();

        Assert.Equal(26, id.Length);
        Assert.All(id, character => Assert.True(Alphabet.Contains(character, StringComparison.Ordinal)));
    }

    [Fact]
    public void SameMomentAndRandomnessGiveTheSameId()
    {
        var moment = new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.Zero);
        var randomness = Encoding.ASCII.GetBytes("0123456789");

        Assert.Equal(
            IndexBuildId.NewId(moment, randomness),
            IndexBuildId.NewId(moment, randomness));
    }

    [Fact]
    public void LaterMomentsSortAfterEarlierOnes()
    {
        var randomness = Encoding.ASCII.GetBytes("0123456789");
        var earlier = IndexBuildId.NewId(new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero), randomness);
        var later = IndexBuildId.NewId(new DateTimeOffset(2026, 10, 24, 20, 0, 1, TimeSpan.Zero), randomness);

        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }

    [Fact]
    public void RunsInOneProcessAreDistinct()
    {
        var ids = Enumerable.Range(0, 1_000).Select(_ => IndexBuildId.NewId()).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(1_000, ids.Count);
    }
}
