using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing.Ranking;
using Xunit;

namespace SharpCoreAgent.Tests.Indexing;

/// <summary>
/// Fusion: two legs agreeing beat one leg alone, position decides the weight, and a silent leg costs nothing.
/// </summary>
public sealed class ReciprocalRankFusionTests
{
    [Fact]
    public void TwoLegsAgreeingOutrankOneLeg()
    {
        var fused = ReciprocalRankFusion.Fuse(
        [
            new RankedLeg(EvidenceLeg.Lexical, [1, 2]),
            new RankedLeg(EvidenceLeg.Vector, [2, 3]),
        ]);

        Assert.Equal([2L, 1L, 3L], fused.Select(candidate => candidate.ChunkId));
        Assert.Equal([EvidenceLeg.Lexical, EvidenceLeg.Vector], fused[0].Legs);
    }

    [Fact]
    public void PositionDecidesTheWeight()
    {
        var fused = ReciprocalRankFusion.Fuse([new RankedLeg(EvidenceLeg.Lexical, [7, 9])]);

        Assert.Equal(ReciprocalRankFusion.Weight(1), fused[0].Score, 6);
        Assert.Equal(ReciprocalRankFusion.Weight(2), fused[1].Score, 6);
        Assert.True(fused[0].Score > fused[1].Score);
    }

    [Fact]
    public void SilentLegContributesNothing()
    {
        var fused = ReciprocalRankFusion.Fuse(
        [
            new RankedLeg(EvidenceLeg.Lexical, []),
            new RankedLeg(EvidenceLeg.Vector, [4]),
        ]);

        var only = Assert.Single(fused);
        Assert.Equal(4, only.ChunkId);
        Assert.Equal(ReciprocalRankFusion.Weight(1), only.Score, 6);
        Assert.Equal([EvidenceLeg.Vector], only.Legs);
    }

    [Fact]
    public void TiesBreakByPassageId()
    {
        // Both passages sit at rank one of their own leg, so only the id can order them.
        var fused = ReciprocalRankFusion.Fuse(
        [
            new RankedLeg(EvidenceLeg.Lexical, [8]),
            new RankedLeg(EvidenceLeg.Vector, [3]),
        ]);

        Assert.Equal([3L, 8L], fused.Select(candidate => candidate.ChunkId));
    }

    [Fact]
    public void EmptyLegsYieldNothing()
    {
        Assert.Empty(ReciprocalRankFusion.Fuse([]));
    }
}
