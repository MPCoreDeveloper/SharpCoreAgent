namespace SharpCoreAgent.Indexing.Retrieval;

/// <summary>One passage the lexical index matched, with its BM25 score.</summary>
/// <param name="Id">The <c>chunks.id</c>.</param>
/// <param name="Score">The BM25 score; higher is better.</param>
internal sealed record LexicalHit(long Id, double Score);

/// <summary>
/// An in-memory BM25 index over the stored <c>chunks.tokens</c> strings.
/// </summary>
/// <remarks>
/// The engine's <c>FullTextIndex</c> is a general-purpose search structure; the index already stores the exact token
/// string the writer produced, so the read side can build the smaller structure it actually queries. The postings are
/// immutable after a build, which is why one query is a dictionary lookup per token and a bounded sort rather than a
/// scan of every passage.
/// </remarks>
internal sealed class LexicalIndex
{
    private const double K1 = 1.2;

    private const double B = 0.75;

    private readonly Dictionary<string, List<Posting>> _postings;
    private readonly Dictionary<long, int> _lengths;
    private readonly double _averageLength;

    private LexicalIndex(Dictionary<string, List<Posting>> postings, Dictionary<long, int> lengths, double averageLength)
    {
        _postings = postings;
        _lengths = lengths;
        _averageLength = averageLength;
    }

    /// <summary>Gets how many passages the index holds.</summary>
    public int DocumentCount => _lengths.Count;

    /// <summary>Builds the index from every stored token string.</summary>
    /// <param name="documents">The <c>chunks.id</c> and <c>chunks.tokens</c> pairs, in id order.</param>
    /// <returns>The immutable index.</returns>
    public static LexicalIndex Build(IEnumerable<(long Id, string Tokens)> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var postings = new Dictionary<string, List<Posting>>(StringComparer.Ordinal);
        var lengths = new Dictionary<long, int>();
        var total = 0L;

        foreach (var (id, tokens) in documents)
        {
            var terms = tokens.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            lengths[id] = terms.Length;
            total += terms.Length;

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var term in terms)
            {
                counts[term] = counts.TryGetValue(term, out var count) ? count + 1 : 1;
            }

            foreach (var (term, frequency) in counts)
            {
                if (!postings.TryGetValue(term, out var list))
                {
                    list = [];
                    postings[term] = list;
                }

                list.Add(new Posting(id, frequency));
            }
        }

        return new LexicalIndex(postings, lengths, lengths.Count == 0 ? 0 : (double)total / lengths.Count);
    }

    /// <summary>Finds the passages that share terms with a query.</summary>
    /// <param name="query">The query's already-tokenised text.</param>
    /// <param name="limit">The most hits to return.</param>
    /// <returns>The best hits, ordered by score and then id.</returns>
    public IReadOnlyList<LexicalHit> Search(string query, int limit)
    {
        if (limit <= 0 || _lengths.Count == 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return [];
        }

        var scores = new Dictionary<long, double>();
        var documentCount = _lengths.Count;
        foreach (var term in terms)
        {
            if (!_postings.TryGetValue(term, out var list) || list.Count == 0)
            {
                continue;
            }

            var idf = Math.Log(1 + ((documentCount - list.Count + 0.5) / (list.Count + 0.5)));
            foreach (var posting in list)
            {
                var length = _lengths[posting.Id];
                var denominator = posting.Frequency + (K1 * (1 - B + (B * length / Math.Max(_averageLength, 1))));
                var score = idf * (posting.Frequency * (K1 + 1) / denominator);
                scores[posting.Id] = scores.TryGetValue(posting.Id, out var existing) ? existing + score : score;
            }
        }

        var hits = new List<LexicalHit>(scores.Count);
        foreach (var (id, score) in scores)
        {
            hits.Add(new LexicalHit(id, Math.Round(score, 6, MidpointRounding.AwayFromZero)));
        }

        hits.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : left.Id.CompareTo(right.Id);
        });

        return hits.Count > limit ? hits.GetRange(0, limit) : hits;
    }

    private sealed record Posting(long Id, int Frequency);
}
