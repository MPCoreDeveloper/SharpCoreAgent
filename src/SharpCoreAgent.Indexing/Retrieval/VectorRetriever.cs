using System.Globalization;
using SharpCoreAgent.Indexing.Embedding;
using SharpCoreAgent.Indexing.Extraction;

namespace SharpCoreAgent.Indexing.Retrieval;

/// <summary>
/// One passage the vector leg matched, with the similarity it earned.
/// </summary>
/// <param name="Chunk">The passage.</param>
/// <param name="Score">Cosine similarity, in <c>[0, 1]</c>; higher is better and never increases down the list.</param>
internal sealed record VectorMatch(ChunkRow Chunk, double Score);

/// <summary>
/// What the vector leg answered, and — when it answered nothing — the sentence that explains why.
/// </summary>
/// <remarks>
/// The note exists because "no semantic results" has three very different causes: vectors were never built
/// (<c>--embedder off</c>), they were built by a different embedder, or the query simply matched nothing. Only the
/// caller can turn that into a line a model can read, and it cannot do so from an empty list.
/// </remarks>
/// <param name="Matches">The matches, best first.</param>
/// <param name="Note">Why the leg was silent, or <see langword="null"/> when it answered.</param>
internal sealed record VectorLegResult(IReadOnlyList<VectorMatch> Matches, string? Note)
{
    /// <summary>
    /// Creates an answer without matches.
    /// </summary>
    /// <param name="note">Why the leg could not search, or <see langword="null"/> when it searched and found nothing.</param>
    /// <returns>The empty answer.</returns>
    public static VectorLegResult Empty(string? note = null) => new([], note);
}


/// <summary>
/// The vector leg: cosine similarity between the query's embedding and the passages the snapshot holds.
/// </summary>
/// <remarks>
/// Three decisions are worth stating.
/// <list type="number">
/// <item>The comparison runs over the vectors the snapshot loaded, in this assembly. SharpCoreDB 2.1.0-RC.3
/// registers <c>vec_distance_cosine</c> but its SQL surface cannot be used for ranking: the vector query optimizer
/// is a stub that declines every plan, a projection that computes a function is ignored and returns whole rows, and
/// a <c>WHERE vec_distance_cosine(...)</c> predicate matches nothing. The scan is therefore done here, behind this
/// one method, so the day the optimizer lands the work can move into the engine without touching the ranker.</item>
/// <item>A similarity floor is applied. Feature-hashed vectors of unrelated text sit near zero by construction, so
/// without a floor the leg would pad the fusion list with orthogonal passages that merely outrank each other.</item>
/// <item>Nothing throws for a leg that cannot run. An index built with the embedder off, or by an embedder of a
/// different width, produces an empty list and a note, because a missing second opinion is not a failed query.</item>
/// </list>
/// The cost is one pass over the passages: 512 multiply-adds each, so a 20 000-passage index answers in single-digit
/// milliseconds and never touches the database file. That linearity is what <c>MaxChunks</c> bounds.
/// </remarks>
internal static class VectorRetriever
{
    /// <summary>How many candidates this leg offers per requested block.</summary>
    internal const int CandidateMultiplier = 4;

    /// <summary>
    /// The similarity below which a match is treated as noise.
    /// </summary>
    /// <remarks>
    /// Chosen against the local 512-dimension hashed embedder, where a passage that shares a handful of words with
    /// the query lands between 0.1 and 0.6 and unrelated text sits under 0.05. A provider embedder, with a much
    /// denser signal, would place everything well above this floor — which only costs candidates the ranker then
    /// reorders, never correctness.
    /// </remarks>
    internal const double MinimumSimilarity = 0.05;

    /// <summary>
    /// Finds the passages whose stored vectors point in the same direction as the query's.
    /// </summary>
    /// <param name="snapshot">The build to search, including the vectors it loaded.</param>
    /// <param name="embedder">The embedder that must produce the query vector.</param>
    /// <param name="text">The query as the caller wrote it.</param>
    /// <param name="k">How many blocks the caller wants; the leg returns up to <c>4 × k</c> candidates.</param>
    /// <returns>The matches, best first, and the note that explains an empty answer.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static VectorLegResult Search(IndexSnapshot snapshot, ICodeTextEmbedder embedder, string text, int k)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(text);

        if (k <= 0)
        {
            return VectorLegResult.Empty();
        }

        if (!snapshot.HasVectors)
        {
            return VectorLegResult.Empty("the index holds no vectors (embedder off); only the lexical leg answered");
        }

        if (embedder.Dimensions != snapshot.Dimensions)
        {
            return VectorLegResult.Empty(string.Create(
                CultureInfo.InvariantCulture,
                $"the index stores {snapshot.Dimensions}-dimension vectors but this embedder produces {embedder.Dimensions}; rebuild with the matching embedder"));
        }

        // A query of punctuation has no direction to compare; answering would rank every passage by rounding noise.
        if (CodeTokenizer.Split(text).Count == 0)
        {
            return VectorLegResult.Empty();
        }

        var query = new float[embedder.Dimensions];
        embedder.Embed(text, query);

        var scored = new List<VectorMatch>();

        foreach (var chunk in snapshot.Chunks.Values)
        {
            if (chunk.Vector is not { Length: > 0 } vector || vector.Length != embedder.Dimensions)
            {
                // A passage written before the embedder was changed, or with vectors off, has no direction to
                // compare; it is skipped rather than scored as zero, because "unknown" and "unrelated" are not the
                // same answer.
                continue;
            }

            var similarity = HashedCodeEmbedder.CosineSimilarity(query, vector);
            if (similarity < MinimumSimilarity)
            {
                continue;
            }

            scored.Add(new VectorMatch(
                chunk,
                Math.Round(Math.Min(similarity, 1d), 6, MidpointRounding.AwayFromZero)));
        }

        // Ids break ties, not insertion order: two identical passages must come back in the same order every run.
        scored.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : left.Chunk.Id.CompareTo(right.Chunk.Id);
        });

        var limit = Math.Min(scored.Count, k * CandidateMultiplier);
        return new VectorLegResult(scored.GetRange(0, limit), null);
    }
}
