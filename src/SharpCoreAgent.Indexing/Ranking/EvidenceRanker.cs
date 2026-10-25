using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Indexing.Extraction;
using SharpCoreAgent.Indexing.Retrieval;

namespace SharpCoreAgent.Indexing.Ranking;

/// <summary>
/// Turns the legs' opinions into one ordered list of citable blocks: fusion, then the boosts that encode what a
/// developer would consider more relevant, then the graph term for anything reached through a relation.
/// </summary>
/// <remarks>
/// The order of operations is the design.
/// <list type="number">
/// <item>Fuse first (<see cref="ReciprocalRankFusion"/>). Position is the only thing the legs agree on, so it has to
/// be the base of the score; boosting before fusion would mean boosting BM25 numbers and cosine numbers by the same
/// factor, which is not the same thing.</item>
/// <item>Then multiply by bounded boosts — an exact declaration name, a type declaration, a path that names the
/// query, documentation that mentions the symbol, a file written minutes ago, and a file the build could not finish.
/// Each is a multiplier rather than an addend, so a boost can never invent relevance where both legs agreed there was
/// none.</item>
/// <item>Then blend the graph term with GraphRAG's 0.5 / 0.3 / 0.2 shape, but only for passages the graph reached:
/// giving every text hit 0.2 for "same community" would inflate them all by the same amount and change nothing except
/// the scale.</item>
/// </list>
/// Nothing here reads a clock: the moment that decides "recently modified" is a parameter, so the same index and the
/// same query produce the same order in a test, in a replay and in production.
/// </remarks>
internal static class EvidenceRanker
{
    /// <summary>How many fused hits seed the graph expansion.</summary>
    internal const int GraphSeedCount = 3;

    /// <summary>The edge weight that counts as a full-strength relation.</summary>
    internal const double MaxEdgeWeight = 5d;

    /// <summary>Multiplier for a passage whose declaration the query names exactly.</summary>
    internal const double ExactSymbolBoost = 1.6;

    /// <summary>Multiplier for a passage that declares a type rather than a member.</summary>
    internal const double TypeBoost = 1.15;

    /// <summary>Multiplier for a passage whose path contains one of the query's words.</summary>
    internal const double PathBoost = 1.10;

    /// <summary>Multiplier for a declaration the workspace's documentation mentions.</summary>
    internal const double DocBoost = 1.05;

    /// <summary>Multiplier for a file written within <see cref="RecentWindow"/>.</summary>
    internal const double RecentBoost = 1.02;

    /// <summary>Multiplier for a file the build skipped, failed on or marked stale.</summary>
    internal const double UnindexedPenalty = 0.50;

    /// <summary>Weight of the fused score in a graph-reached passage's final score.</summary>
    internal const double SemanticWeight = 0.5;

    /// <summary>Weight of the relation strength in a graph-reached passage's final score.</summary>
    internal const double TopologyWeight = 0.3;

    /// <summary>Weight of "same community as a seed" in a graph-reached passage's final score.</summary>
    internal const double CommunityWeight = 0.2;

    /// <summary>How recently a file must have been written to earn the recency boost.</summary>
    internal static TimeSpan RecentWindow { get; } = TimeSpan.FromMinutes(10);

    /// <summary>The languages that answer the <c>doc</c> query kind.</summary>
    private static readonly HashSet<string> DocLanguages = new(StringComparer.OrdinalIgnoreCase) { "markdown" };

    /// <summary>The languages that answer the <c>config</c> query kind.</summary>
    private static readonly HashSet<string> ConfigLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "yaml", "toml", "xml",
    };

    /// <summary>
    /// Ranks the passages two legs produced.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="query">The validated request; its kind, path and hop count decide what may be returned.</param>
    /// <param name="lexical">The lexical leg's matches, best first.</param>
    /// <param name="vector">The vector leg's matches, best first.</param>
    /// <param name="nowUtc">The moment the query runs, used only for the recency boost.</param>
    /// <returns>The blocks, best first; empty when neither leg matched anything eligible.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static IReadOnlyList<CodeEvidence> Rank(
        IndexSnapshot snapshot,
        CodeQuery query,
        IReadOnlyList<LexicalMatch> lexical,
        IReadOnlyList<VectorMatch> vector,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(lexical);
        ArgumentNullException.ThrowIfNull(vector);

        var symbolsByKey = BuildSymbolIndex(snapshot);
        var fused = ReciprocalRankFusion.Fuse(
        [
            new RankedLeg(EvidenceLeg.Lexical, Eligible(lexical, snapshot, query, symbolsByKey)),
            new RankedLeg(EvidenceLeg.Vector, Eligible(vector, snapshot, query, symbolsByKey)),
        ]);

        if (fused.Count == 0)
        {
            return [];
        }

        var candidates = CreateCandidates(fused, snapshot);
        ApplyGraph(snapshot, query, fused, candidates, symbolsByKey);

        var tokens = CodeTokenizer.Split(query.Text);
        var maxFusion = fused[0].Score;
        var recentAfter = nowUtc.ToUniversalTime() - RecentWindow;

        foreach (var candidate in candidates.Values)
        {
            var semantic = maxFusion > 0 ? candidate.Fusion / maxFusion : 0;
            var boosted = semantic * Boost(candidate.Chunk, snapshot, tokens, recentAfter, symbolsByKey);

            candidate.Score = ReciprocalRankFusion.Round(candidate.Legs.Contains(EvidenceLeg.Graph)
                ? (SemanticWeight * boosted) + (TopologyWeight * candidate.Topology) + (CommunityWeight * candidate.Community)
                : boosted);
        }

        var ranked = candidates.Values.ToList();
        ranked.Sort(CandidateOrder.Instance);

        return Merge([.. ranked.Select(candidate => ToEvidence(candidate, snapshot, symbolsByKey))]);
    }

    /// <summary>
    /// Keeps a lexical leg's order but drops everything the request excludes.
    /// </summary>
    /// <param name="matches">The leg's matches, best first.</param>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="query">The request, whose <c>path</c> and <c>kind</c> are the filter.</param>
    /// <param name="symbolsByKey">The declarations, by key.</param>
    /// <returns>The matching chunk ids, in the leg's order.</returns>
    private static List<long> Eligible(
        IReadOnlyList<LexicalMatch> matches,
        IndexSnapshot snapshot,
        CodeQuery query,
        IReadOnlyDictionary<string, SymbolRow> symbolsByKey) =>
        [.. matches
            .Select(match => match.Chunk)
            .Where(chunk => Matches(chunk, snapshot, query, symbolsByKey))
            .Select(chunk => chunk.Id)];

    /// <summary>
    /// Keeps a vector leg's order but drops everything the request excludes.
    /// </summary>
    /// <param name="matches">The leg's matches, best first.</param>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="query">The request, whose <c>path</c> and <c>kind</c> are the filter.</param>
    /// <param name="symbolsByKey">The declarations, by key.</param>
    /// <returns>The matching chunk ids, in the leg's order.</returns>
    private static List<long> Eligible(
        IReadOnlyList<VectorMatch> matches,
        IndexSnapshot snapshot,
        CodeQuery query,
        IReadOnlyDictionary<string, SymbolRow> symbolsByKey) =>
        [.. matches
            .Select(match => match.Chunk)
            .Where(chunk => Matches(chunk, snapshot, query, symbolsByKey))
            .Select(chunk => chunk.Id)];

    /// <summary>
    /// Decides whether one passage is inside the request's path and kind.
    /// </summary>
    /// <param name="chunk">The passage.</param>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="query">The request.</param>
    /// <param name="symbolsByKey">The declarations, by key.</param>
    /// <returns><see langword="true"/> when the passage may be returned.</returns>
    private static bool Matches(
        ChunkRow chunk,
        IndexSnapshot snapshot,
        CodeQuery query,
        IReadOnlyDictionary<string, SymbolRow> symbolsByKey)
    {
        if (query.Path is { } path
            && !string.Equals(chunk.Path, path, StringComparison.Ordinal)
            && !chunk.Path.StartsWith(path + "/", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(query.Kind, CodeQuery.AnyKind, StringComparison.Ordinal))
        {
            return true;
        }

        var language = LanguageOf(chunk, snapshot);

        if (string.Equals(query.Kind, "doc", StringComparison.Ordinal))
        {
            return DocLanguages.Contains(language);
        }

        if (string.Equals(query.Kind, "config", StringComparison.Ordinal))
        {
            return ConfigLanguages.Contains(language);
        }

        // A declaration kind asks about the symbol the passage opens, not about the shape of the block: a `method`
        // passage is a member chunk whose declaration is a method, and those are two different questions.
        return chunk.SymbolKey is { } key
            && symbolsByKey.TryGetValue(key, out var symbol)
            && string.Equals(symbol.Kind, query.Kind, StringComparison.Ordinal);
    }

    /// <summary>
    /// Indexes the declarations by key, keeping the lowest id when a rebuild left two rows with one key.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <returns>The declarations, by key.</returns>
    private static Dictionary<string, SymbolRow> BuildSymbolIndex(IndexSnapshot snapshot)
    {
        var symbols = new Dictionary<string, SymbolRow>(StringComparer.Ordinal);

        foreach (var symbol in snapshot.Symbols.Values)
        {
            if (!symbols.TryGetValue(symbol.Key, out var existing) || symbol.Id < existing.Id)
            {
                symbols[symbol.Key] = symbol;
            }
        }

        return symbols;
    }

    /// <summary>
    /// Reads the language of the file a passage belongs to.
    /// </summary>
    /// <param name="chunk">The passage.</param>
    /// <param name="snapshot">The build being searched.</param>
    /// <returns>The language, or the empty string when the file row is gone.</returns>
    private static string LanguageOf(ChunkRow chunk, IndexSnapshot snapshot) =>
        snapshot.Files.TryGetValue(chunk.FileId, out var file) ? file.Language : string.Empty;

    /// <summary>
    /// Turns the fused list into the candidates that will be scored.
    /// </summary>
    /// <param name="fused">The fused passages, best first.</param>
    /// <param name="snapshot">The build being searched.</param>
    /// <returns>The candidates, keyed by passage id.</returns>
    private static Dictionary<long, Candidate> CreateCandidates(
        IReadOnlyList<FusedCandidate> fused,
        IndexSnapshot snapshot)
    {
        var candidates = new Dictionary<long, Candidate>(fused.Count);

        foreach (var entry in fused)
        {
            if (!snapshot.Chunks.TryGetValue(entry.ChunkId, out var chunk))
            {
                continue;
            }

            var candidate = new Candidate(chunk) { Fusion = entry.Score };
            candidate.Legs.AddRange(entry.Legs);
            candidates[entry.ChunkId] = candidate;
        }

        return candidates;
    }

    /// <summary>
    /// Adds the passages the symbol graph reaches from the best fused hits.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="query">The request, whose <c>hops</c> bounds the walk.</param>
    /// <param name="fused">The fused passages, best first; the first few that open a declaration are the seeds.</param>
    /// <param name="candidates">The candidates to extend, keyed by passage id.</param>
    /// <param name="symbolsByKey">The declarations, by key.</param>
    private static void ApplyGraph(
        IndexSnapshot snapshot,
        CodeQuery query,
        IReadOnlyList<FusedCandidate> fused,
        Dictionary<long, Candidate> candidates,
        Dictionary<string, SymbolRow> symbolsByKey)
    {
        if (query.Hops <= 0 || snapshot.Edges.Count == 0)
        {
            return;
        }

        var seeds = FindSeeds(snapshot, fused, symbolsByKey);
        if (seeds.Count == 0)
        {
            return;
        }

        var reached = Walk(BuildAdjacency(snapshot), seeds, query.Hops);
        if (reached.Count == 0)
        {
            return;
        }

        AttachReached(snapshot, query, reached, candidates, symbolsByKey, CommunitiesOf(seeds));
    }

    /// <summary>
    /// Picks the fused hits that open a declaration, which are the seeds of the walk.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="fused">The fused passages, best first.</param>
    /// <param name="symbolsByKey">The declarations, by key.</param>
    /// <returns>At most <see cref="GraphSeedCount"/> declarations.</returns>
    private static List<SymbolRow> FindSeeds(
        IndexSnapshot snapshot,
        IReadOnlyList<FusedCandidate> fused,
        Dictionary<string, SymbolRow> symbolsByKey)
    {
        var seeds = new List<SymbolRow>(GraphSeedCount);

        foreach (var entry in fused)
        {
            if (seeds.Count == GraphSeedCount)
            {
                break;
            }

            if (snapshot.Chunks.TryGetValue(entry.ChunkId, out var chunk)
                && chunk.SymbolKey is { } key
                && symbolsByKey.TryGetValue(key, out var symbol))
            {
                seeds.Add(symbol);
            }
        }

        return seeds;
    }

    /// <summary>
    /// Collects the community labels of the seeds, which is what "same community" is measured against.
    /// </summary>
    /// <param name="seeds">The declarations the query matched.</param>
    /// <returns>The labels; empty when no build assigned communities yet.</returns>
    private static HashSet<string> CommunitiesOf(IReadOnlyList<SymbolRow> seeds)
    {
        var communities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var seed in seeds)
        {
            if (seed.Community is { Length: > 0 } community)
            {
                communities.Add(community);
            }
        }

        return communities;
    }

    /// <summary>
    /// Turns the reached nodes into candidates: the passage that declares each one, with its relation and community.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="query">The request, whose filters also apply to graph results.</param>
    /// <param name="reached">The nodes the walk reached, by symbol id.</param>
    /// <param name="candidates">The candidates to extend.</param>
    /// <param name="symbolsByKey">The declarations, by key.</param>
    /// <param name="communities">The seeds' community labels.</param>
    private static void AttachReached(
        IndexSnapshot snapshot,
        CodeQuery query,
        Dictionary<long, ReachedPath> reached,
        Dictionary<long, Candidate> candidates,
        Dictionary<string, SymbolRow> symbolsByKey,
        HashSet<string> communities)
    {
        var chunkBySymbolKey = BuildChunkIndex(snapshot);

        foreach (var symbol in snapshot.Symbols.Values.OrderBy(symbol => symbol.Id))
        {
            if (!reached.TryGetValue(symbol.Id, out var path)
                || !chunkBySymbolKey.TryGetValue(symbol.Key, out var chunkId))
            {
                continue;
            }

            var chunk = snapshot.Chunks[chunkId];
            if (!Matches(chunk, snapshot, query, symbolsByKey))
            {
                continue;
            }

            var candidate = GetOrAdd(candidates, chunk);
            candidate.AddLeg(EvidenceLeg.Graph);

            if (path.Topology > candidate.Topology)
            {
                candidate.Topology = path.Topology;
                candidate.Relation = path.Relation;
            }

            if (symbol.Community is { Length: > 0 } label && communities.Contains(label))
            {
                candidate.Community = 1d;
            }
        }
    }

    private static Candidate GetOrAdd(Dictionary<long, Candidate> candidates, ChunkRow chunk)
    {
        if (candidates.TryGetValue(chunk.Id, out var existing))
        {
            return existing;
        }

        var candidate = new Candidate(chunk);
        candidates[chunk.Id] = candidate;
        return candidate;
    }

    /// <summary>
    /// Builds the walkable graph from the stored edges, in both directions.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <returns>Every node's neighbours, with the relation as the walker would see it.</returns>
    private static Dictionary<long, List<Neighbour>> BuildAdjacency(IndexSnapshot snapshot)
    {
        var adjacency = new Dictionary<long, List<Neighbour>>(snapshot.SymbolCount);

        foreach (var edge in snapshot.Edges)
        {
            Add(adjacency, edge.Source, new Neighbour(edge.Target, edge.Relation, edge.Weight));
            Add(adjacency, edge.Target, new Neighbour(edge.Source, Reverse(edge.Relation), edge.Weight));
        }

        return adjacency;
    }

    private static void Add(Dictionary<long, List<Neighbour>> adjacency, long node, Neighbour neighbour)
    {
        if (!adjacency.TryGetValue(node, out var neighbours))
        {
            neighbours = [];
            adjacency[node] = neighbours;
        }

        neighbours.Add(neighbour);
    }

    /// <summary>
    /// Names the edge from the neighbour's side.
    /// </summary>
    /// <param name="relation">The stored relation.</param>
    /// <returns><c>called_by</c> for a reversed call, the same relation otherwise.</returns>
    private static string Reverse(string relation) =>
        string.Equals(relation, RelationKinds.Calls, StringComparison.Ordinal) ? CalledBy : relation;

    /// <summary>
    /// Walks the graph from the seeds, keeping the strongest path to every node.
    /// </summary>
    /// <param name="adjacency">The neighbours of every node.</param>
    /// <param name="seeds">The declarations the query itself matched.</param>
    /// <param name="hops">How many edges a path may take.</param>
    /// <returns>The reached nodes, with the strength and relation of the path that reached them.</returns>
    private static Dictionary<long, ReachedPath> Walk(
        Dictionary<long, List<Neighbour>> adjacency,
        List<SymbolRow> seeds,
        int hops)
    {
        var reached = new Dictionary<long, ReachedPath>();
        var seen = new HashSet<long>();
        var frontier = new List<NodePath>(seeds.Count);

        seen.UnionWith(seeds.Select(seed => seed.Id));
        frontier.AddRange(seeds.Select(seed => new NodePath(seed.Id, 1d, string.Empty)));

        for (var depth = 0; depth < hops && frontier.Count > 0; depth++)
        {
            frontier = Expand(frontier, adjacency, seen, reached);
        }

        return reached;
    }

    /// <summary>
    /// Expands one breadth-first level: every unseen neighbour of the frontier, with its path strength and relation.
    /// </summary>
    /// <param name="frontier">The nodes reached in the previous level.</param>
    /// <param name="adjacency">The neighbours of every node.</param>
    /// <param name="seen">The nodes already reached; extended with the new level.</param>
    /// <param name="reached">The best path to every node; extended with the new level.</param>
    /// <returns>The new level, which is empty when the frontier has no unseen neighbours.</returns>
    private static List<NodePath> Expand(
        List<NodePath> frontier,
        Dictionary<long, List<Neighbour>> adjacency,
        HashSet<long> seen,
        Dictionary<long, ReachedPath> reached)
    {
        var next = new List<NodePath>();

        foreach (var node in frontier)
        {
            if (!adjacency.TryGetValue(node.Symbol, out var neighbours))
            {
                continue;
            }

            foreach (var neighbour in neighbours)
            {
                if (seen.Contains(neighbour.Target))
                {
                    continue;
                }

                // Every step multiplies by at most one, so the first path to a node is also the strongest one a
                // breadth-first walk can find; a longer path could only be weaker.
                var topology = node.Topology * Math.Clamp(neighbour.Weight / MaxEdgeWeight, 0d, 1d);
                var relation = node.Relation.Length == 0 ? neighbour.Relation : node.Relation;

                reached[neighbour.Target] = new ReachedPath(topology, relation);
                next.Add(new NodePath(neighbour.Target, topology, relation));
            }
        }

        seen.UnionWith(next.Select(node => node.Symbol));
        return next;
    }

    /// <summary>
    /// Indexes the passages by the declaration they open, keeping the first one of a symbol.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <returns>The passage id that cites each declaration.</returns>
    private static Dictionary<string, long> BuildChunkIndex(IndexSnapshot snapshot)
    {
        var chunks = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var chunk in snapshot.Chunks.Values.OrderBy(chunk => chunk.Id))
        {
            if (chunk.SymbolKey is { Length: > 0 } key)
            {
                chunks.TryAdd(key, chunk.Id);
            }
        }

        return chunks;
    }

    /// <summary>
    /// Computes the product of every boost that applies to one passage.
    /// </summary>
    /// <param name="chunk">The passage.</param>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="tokens">The query's words.</param>
    /// <param name="recentAfter">The moment that separates "recently modified" from "not".</param>
    /// <param name="symbolsByKey">The declarations, by key.</param>
    /// <returns>The multiplier, never zero.</returns>
    private static double Boost(
        ChunkRow chunk,
        IndexSnapshot snapshot,
        IReadOnlyList<string> tokens,
        DateTimeOffset recentAfter,
        Dictionary<string, SymbolRow> symbolsByKey)
    {
        var boost = SymbolBoost(chunk, snapshot, tokens, symbolsByKey);

        if (PathMentions(chunk.Path, tokens))
        {
            boost *= PathBoost;
        }

        return boost * FileBoost(snapshot, chunk.FileId, recentAfter);
    }

    /// <summary>
    /// Computes the boosts that come from the declaration a passage opens and from what the workspace says about it.
    /// </summary>
    /// <param name="chunk">The passage.</param>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="tokens">The query's words.</param>
    /// <param name="symbolsByKey">The declarations, by key.</param>
    /// <returns>The multiplier; 1 when the passage opens no declaration.</returns>
    private static double SymbolBoost(
        ChunkRow chunk,
        IndexSnapshot snapshot,
        IReadOnlyList<string> tokens,
        Dictionary<string, SymbolRow> symbolsByKey)
    {
        if (chunk.SymbolKey is not { } key || chunk.SymbolName is not { Length: > 0 } name)
        {
            return 1d;
        }

        var boost = 1d;

        // The short name and the qualified name are both accepted: a model asks for "Area" as often as for
        // "Demo.Geometry.Circle.Area", and both mean the same declaration.
        var shortName = name[(name.LastIndexOf('.') + 1)..];
        if (tokens.Any(token =>
            string.Equals(token, shortName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, name, StringComparison.OrdinalIgnoreCase)))
        {
            boost *= ExactSymbolBoost;
        }

        if (!symbolsByKey.TryGetValue(key, out var symbol))
        {
            return boost;
        }

        if (IsType(symbol.Kind))
        {
            boost *= TypeBoost;
        }

        if (snapshot.DocumentedSymbols.Contains(key))
        {
            boost *= DocBoost;
        }

        return boost;
    }

    /// <summary>
    /// Computes the boosts that come from the file a passage belongs to.
    /// </summary>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="fileId">The owning file.</param>
    /// <param name="recentAfter">The moment that separates "recently modified" from "not".</param>
    /// <returns>The multiplier; 1 when the file row is gone.</returns>
    private static double FileBoost(IndexSnapshot snapshot, long fileId, DateTimeOffset recentAfter)
    {
        if (!snapshot.Files.TryGetValue(fileId, out var file))
        {
            return 1d;
        }

        var boost = 1d;

        if (file.ModifiedUtc is { } modified && modified >= recentAfter)
        {
            boost *= RecentBoost;
        }

        if (!file.IsIndexed)
        {
            boost *= UnindexedPenalty;
        }

        return boost;
    }

    /// <summary>
    /// Decides whether any query word appears in a path.
    /// </summary>
    /// <param name="path">The workspace-relative path.</param>
    /// <param name="tokens">The query's words.</param>
    /// <returns><see langword="true"/> when a directory or file name is one of the query's words.</returns>
    private static bool PathMentions(string path, IReadOnlyList<string> tokens) =>
        path.Split(['/', '.', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Any(word => tokens.Any(token => string.Equals(word, token, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Decides whether a declaration kind is a type.
    /// </summary>
    /// <param name="kind">The stored kind.</param>
    /// <returns><see langword="true"/> for a class, interface, record, struct or enum.</returns>
    private static bool IsType(string kind) =>
        string.Equals(kind, SymbolKinds.Class, StringComparison.Ordinal)
        || string.Equals(kind, SymbolKinds.Interface, StringComparison.Ordinal)
        || string.Equals(kind, SymbolKinds.Record, StringComparison.Ordinal)
        || string.Equals(kind, SymbolKinds.Struct, StringComparison.Ordinal)
        || string.Equals(kind, SymbolKinds.Enum, StringComparison.Ordinal);

    /// <summary>
    /// Turns one scored candidate into the block a caller reads.
    /// </summary>
    /// <param name="candidate">The scored candidate.</param>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="symbolsByKey">The declarations, by key.</param>
    /// <returns>The block.</returns>
    private static CodeEvidence ToEvidence(
        Candidate candidate,
        IndexSnapshot snapshot,
        IReadOnlyDictionary<string, SymbolRow> symbolsByKey)
    {
        var chunk = candidate.Chunk;
        var legs = candidate.Legs.ToList();
        legs.Sort();

        return new CodeEvidence
        {
            Path = chunk.Path,
            StartLine = chunk.StartLine,
            EndLine = chunk.EndLine,
            Sha8 = chunk.Sha8,
            Text = chunk.Text,
            Kind = KindOf(chunk, snapshot, symbolsByKey),
            Symbol = chunk.SymbolName,
            Score = candidate.Score,
            Legs = legs,
            IsStale = snapshot.Files.TryGetValue(chunk.FileId, out var file) && file.IsStale,
            Relation = candidate.Relation,
        };
    }

    /// <summary>
    /// Names what a passage was selected as.
    /// </summary>
    /// <param name="chunk">The passage.</param>
    /// <param name="snapshot">The build being searched.</param>
    /// <param name="symbolsByKey">The declarations, by key.</param>
    /// <returns>A declaration kind, <c>doc</c>, <c>config</c> or <c>chunk</c>.</returns>
    private static string KindOf(
        ChunkRow chunk,
        IndexSnapshot snapshot,
        IReadOnlyDictionary<string, SymbolRow> symbolsByKey)
    {
        if (chunk.SymbolKey is { } key && symbolsByKey.TryGetValue(key, out var symbol))
        {
            return symbol.Kind;
        }

        var language = LanguageOf(chunk, snapshot);

        if (DocLanguages.Contains(language))
        {
            return "doc";
        }

        return ConfigLanguages.Contains(language) ? "config" : "chunk";
    }

    /// <summary>
    /// Joins passages that continue each other in the file and sit next to each other in the result.
    /// </summary>
    /// <param name="blocks">The ranked blocks.</param>
    /// <returns>The blocks, with touching neighbours of one declaration folded together.</returns>
    /// <remarks>
    /// A 120-line class is three passages; returning them as three citations costs three headings for one location.
    /// Only neighbours in the ranking are joined, because folding a block into another one that something else was
    /// ranked between would move evidence the model has already been told about.
    /// </remarks>
    private static List<CodeEvidence> Merge(List<CodeEvidence> blocks)
    {
        if (blocks.Count < 2)
        {
            return blocks;
        }

        var merged = new List<CodeEvidence>(blocks.Count);

        foreach (var block in blocks)
        {
            if (merged.Count > 0 && Touches(merged[^1], block))
            {
                merged[^1] = Join(merged[^1], block);
                continue;
            }

            merged.Add(block);
        }

        return merged;
    }

    private static bool Touches(CodeEvidence previous, CodeEvidence next) =>
        string.Equals(previous.Path, next.Path, StringComparison.Ordinal)
        && previous.EndLine + 1 == next.StartLine
        && string.Equals(previous.Symbol, next.Symbol, StringComparison.Ordinal);

    private static CodeEvidence Join(CodeEvidence first, CodeEvidence second)
    {
        var text = first.Text + "\n" + second.Text;
        var legs = first.Legs.Concat(second.Legs).Distinct().Order().ToArray();

        return first with
        {
            EndLine = second.EndLine,
            Text = text,
            Legs = legs,
            Score = Math.Max(first.Score, second.Score),
            Sha8 = IndexFileContent.HashText(text)[..8],
        };
    }

    /// <summary>The label a block carries when it was reached by reading a <c>calls</c> edge backwards.</summary>
    private const string CalledBy = "called_by";

    /// <summary>One scored passage, while it is still being scored.</summary>
    private sealed class Candidate(ChunkRow chunk)
    {
        public ChunkRow Chunk { get; } = chunk;

        public double Fusion { get; init; }

        public List<EvidenceLeg> Legs { get; } = [];

        public void AddLeg(EvidenceLeg leg)
        {
            if (!Legs.Contains(leg))
            {
                Legs.Add(leg);
            }
        }

        public double Score { get; set; }

        public double Topology { get; set; }

        public double Community { get; set; }

        public string? Relation { get; set; }
    }

    /// <summary>The order every caller sees: score, then path, then line.</summary>
    private sealed class CandidateOrder : IComparer<Candidate>
    {
        public static CandidateOrder Instance { get; } = new();

        public int Compare(Candidate? left, Candidate? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return 1;
            }

            if (right is null)
            {
                return -1;
            }

            var byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var byPath = string.CompareOrdinal(left.Chunk.Path, right.Chunk.Path);
            if (byPath != 0)
            {
                return byPath;
            }

            var byLine = left.Chunk.StartLine.CompareTo(right.Chunk.StartLine);
            return byLine != 0 ? byLine : left.Chunk.Id.CompareTo(right.Chunk.Id);
        }
    }

    private readonly record struct Neighbour(long Target, string Relation, double Weight);

    private readonly record struct NodePath(long Symbol, double Topology, string Relation);

    private readonly record struct ReachedPath(double Topology, string Relation);
}


