namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// How much detail the index keeps.
/// </summary>
/// <remarks>
/// <see cref="Lean"/> is the default and the only tier this feature ships: passages, declarations and
/// edges, with a local embedder. <see cref="Rich"/> is reserved for a later milestone that adds a
/// provider embedder and wider excerpts; naming it now keeps the metadata column honest instead of
/// adding a boolean later.
/// </remarks>
public enum IndexTier
{
    /// <summary>Passages, declarations, edges and 512-dimension local vectors.</summary>
    Lean,

    /// <summary>A wider, costlier build; not produced by this version.</summary>
    Rich,
}

/// <summary>
/// Where passage and symbol vectors come from.
/// </summary>
public enum EmbedderKind
{
    /// <summary>The built-in deterministic hashed embedder; no network, no credential.</summary>
    Local,

    /// <summary>An embedding endpoint of the configured provider; opt-in and costlier.</summary>
    Provider,

    /// <summary>No vectors are produced or read; lexical and graph retrieval still work.</summary>
    Off,
}

/// <summary>
/// How vectors are stored at rest.
/// </summary>
public enum VectorQuantization
{
    /// <summary>Full-precision floats.</summary>
    None,

    // CA1720 sees a type name in `Int8`, and it is the name this tier has to have: the CLI flag and the
    // meta row both spell it that way, and `Byte` would be the same warning with a worse word.
#pragma warning disable CA1720
    /// <summary>One byte per dimension, with a per-vector scale.</summary>
    Int8,
#pragma warning restore CA1720

    /// <summary>One bit per dimension; smallest and least precise.</summary>
    Binary,
}

/// <summary>
/// What the index believes about itself right now.
/// </summary>
/// <remarks>
/// One value answers both questions a caller has — "can I use it?" and "can I trust it?" — so no tool
/// has to combine flags. The adapter reports <see cref="Building"/> from a failed lock attempt rather
/// than storing it, because a build that has not finished has not written anything yet.
/// </remarks>
public enum IndexFreshness
{
    /// <summary>No database exists for this workspace; the next build creates one.</summary>
    Missing,

    /// <summary>Another process holds the writer lock; the last complete build is still readable.</summary>
    Building,

    /// <summary>A budget stopped the build early; some files are not indexed yet.</summary>
    Partial,

    /// <summary>Files changed after the build; answers may cite outdated line ranges.</summary>
    Stale,

    /// <summary>The index matches the workspace.</summary>
    Fresh,

    /// <summary>The database exists but was rejected on open; a rebuild is required.</summary>
    Corrupt,
}

/// <summary>
/// Direction of a reference query.
/// </summary>
public enum ReferenceDirection
{
    /// <summary>Symbols that call the subject.</summary>
    Callers,

    /// <summary>Symbols the subject calls.</summary>
    Callees,

    /// <summary>Both sides, each block labelled with the side it is on.</summary>
    Both,
}

/// <summary>
/// Which retrieval leg produced (or helped produce) a block.
/// </summary>
/// <remarks>
/// Evidence carries the set rather than a single winner, because the interesting result is usually the
/// one two legs agree on; <c>why: lexical,vector</c> in a tool answer is this enum rendered.
/// </remarks>
public enum EvidenceLeg
{
    /// <summary>Full-text (BM25) retrieval over passages.</summary>
    Lexical,

    /// <summary>Cosine similarity over passage vectors.</summary>
    Vector,

    /// <summary>Reached through the symbol graph, not through text.</summary>
    Graph,

    /// <summary>Reached by matching a declaration name.</summary>
    Symbol,

    /// <summary>Reached because the path filter or a neighbour's file matched.</summary>
    Path,
}
