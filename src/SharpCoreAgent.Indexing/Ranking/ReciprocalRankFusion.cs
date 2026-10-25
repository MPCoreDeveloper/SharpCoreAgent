using SharpCoreAgent.Domain.Index;

namespace SharpCoreAgent.Indexing.Ranking;

/// <summary>
/// One leg's answer, reduced to what fusion needs: which passage ids it ranked, in its own order.
/// </summary>
/// <param name="Leg">The leg that produced the order.</param>
/// <param name="ChunkIds">The passage ids, best first.</param>
internal sealed record RankedLeg(EvidenceLeg Leg, IReadOnlyList<long> ChunkIds);

/// <summary>
/// One passage after fusion: the fused score and every leg that found it.
/// </summary>
/// <param name="ChunkId">The passage.</param>
/// <param name="Score">The sum of <c>1 / (K + rank)</c> over the legs that returned it.</param>
/// <param name="Legs">The legs that returned it, in <see cref="EvidenceLeg"/> order.</param>
internal sealed record FusedCandidate(long ChunkId, double Score, IReadOnlyList<EvidenceLeg> Legs);

/// <summary>
/// Reciprocal-rank fusion: the way two rankings that share no scale are combined.
/// </summary>
/// <remarks>
/// BM25 scores are unbounded, cosine similarity lives in <c>[0, 1]</c>, and a graph hop has a weight rather than a
/// score. Adding those together would mean inventing a normalisation per leg and re-inventing it whenever a leg
/// changes. Fusion only looks at <em>position</em>: a passage at rank <c>r</c> contributes <c>1 / (K + r)</c>, so the
/// best result of each leg is worth the same, agreement between two legs adds up, and a leg that returns nothing
/// simply contributes nothing.
/// <para>
/// <c>K = 60</c> is the constant SharpCoreDB's own hybrid engine uses. It is large enough that the difference between
/// rank 1 and rank 2 is small compared with the gain from a second leg finding the same passage, which is the effect
/// the fusion is for.
/// </para>
/// Scores are rounded to six decimals before they are compared, so two runs cannot disagree about an ordering that
/// only differs in floating-point noise.
/// </remarks>
internal static class ReciprocalRankFusion
{
    /// <summary>The rank constant of the fusion formula.</summary>
    internal const int K = 60;

    /// <summary>
    /// Fuses the legs of one query.
    /// </summary>
    /// <param name="legs">The legs, in <see cref="EvidenceLeg"/> order.</param>
    /// <returns>The candidates, best first, with ties broken by passage id.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="legs"/> is null.</exception>
    public static IReadOnlyList<FusedCandidate> Fuse(IReadOnlyList<RankedLeg> legs)
    {
        ArgumentNullException.ThrowIfNull(legs);

        var scores = new Dictionary<long, double>();
        var found = new Dictionary<long, List<EvidenceLeg>>();
        var order = new List<long>();

        foreach (var leg in legs)
        {
            for (var index = 0; index < leg.ChunkIds.Count; index++)
            {
                var id = leg.ChunkIds[index];
                var weight = Weight(index + 1);

                if (scores.TryGetValue(id, out var existing))
                {
                    scores[id] = existing + weight;
                }
                else
                {
                    scores[id] = weight;
                    order.Add(id);
                    found[id] = [];
                }

                if (!found[id].Contains(leg.Leg))
                {
                    found[id].Add(leg.Leg);
                }
            }
        }

        var candidates = new List<FusedCandidate>(order.Count);
        foreach (var id in order)
        {
            var legsForId = found[id];
            legsForId.Sort();
            candidates.Add(new FusedCandidate(id, Round(scores[id]), legsForId.ToArray()));
        }

        candidates.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : left.ChunkId.CompareTo(right.ChunkId);
        });

        return candidates;
    }

    /// <summary>
    /// The contribution of one rank to a passage's fused score.
    /// </summary>
    /// <param name="rank">The 1-based position in the leg's own list.</param>
    /// <returns><c>1 / (K + rank)</c>.</returns>
    internal static double Weight(int rank) => 1d / (K + rank);

    /// <summary>
    /// Rounds a score the way every comparison in this assembly sees it.
    /// </summary>
    /// <param name="value">The raw score.</param>
    /// <returns>The score to six decimals.</returns>
    internal static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
