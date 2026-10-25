using SharpCoreAgent.Indexing.Extraction;

namespace SharpCoreAgent.Indexing.Retrieval;

/// <summary>
/// One passage the lexical leg matched, with the score it earned.
/// </summary>
/// <param name="Chunk">The passage.</param>
/// <param name="Score">The BM25 score; higher is better and scores never increase down a result list.</param>
internal sealed record LexicalMatch(ChunkRow Chunk, double Score);

/// <summary>
/// The lexical leg: BM25 over the passages of one build, always returning more candidates than the caller asked
/// for so the ranker can fuse it with the other legs.
/// </summary>
/// <remarks>
/// Two rules are worth stating because they are what makes this leg trustworthy.
/// <list type="number">
/// <item>Nothing is searched when the query has no signal: a stop-word-only question ("how does this work")
/// returns an empty list instead of every passage that happens to contain "this".</item>
/// <item>The engine's analyzer reads the query and the stored tokens the same way, so an identifier split into
/// words at index time is split into the same words at query time.</item>
/// </list>
/// The candidate count is four times what the caller asked for, which gives the fusion step something to
/// reorder; a leg that returned exactly <c>k</c> would decide the answer on its own.
/// </remarks>
internal static class LexicalRetriever
{
    /// <summary>How many candidates this leg offers per requested block.</summary>
    internal const int CandidateMultiplier = 4;

    /// <summary>
    /// Finds the passages that share words with a query.
    /// </summary>
    /// <param name="snapshot">The build to search.</param>
    /// <param name="text">The query as the caller wrote it.</param>
    /// <param name="k">How many blocks the caller wants; the leg returns up to <c>4 × k</c> candidates.</param>
    /// <returns>The matches, best first; empty when the query carries no searchable word.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> or <paramref name="text"/> is null.</exception>
    public static IReadOnlyList<LexicalMatch> Search(IndexSnapshot snapshot, string text, int k)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(text);

        if (k <= 0)
        {
            return [];
        }

        var query = CodeTokenizer.TokenizeForQuery(text);
        if (query.Length == 0)
        {
            return [];
        }

        var matches = new List<LexicalMatch>();

        foreach (var hit in snapshot.Lexical.Search(query, k * CandidateMultiplier))
        {
            // A hit whose passage vanished between the load and the query is dropped rather than reported with an
            // empty citation: the snapshot is immutable, but the ids it holds come from a table someone may
            // have rebuilt in another process.
            if (snapshot.Chunks.TryGetValue(hit.Id, out var chunk))
            {
                matches.Add(new LexicalMatch(chunk, hit.Score));
            }
        }

        return matches;
    }
}
