namespace SharpCoreAgent.Indexing;

/// <summary>
/// The steps one build is divided into, and the order a report lists them in.
/// </summary>
/// <remarks>
/// The names are the ones <see cref="SharpCoreAgent.Domain.Index.IndexPhase.Name"/> carries and the command line
/// prints, so they are part of what a developer reads rather than an internal label. Each name covers one decision
/// the build makes, and the split follows the question a slow run asks: was it the walk, the rows, the graph, the
/// clustering, the bookkeeping, or the store underneath all of them.
/// <para>
/// Two phases are not one decision but a group of them, and each is followed by its own steps: the clustering by five
/// (read the declarations, read the relations, partition, rank, write back). A step like that is marked nested rather
/// than being interleaved with the phases that contain it, because its time is already inside its parent's.
/// </para>
/// <para>
/// <see cref="Commit"/> is the widest of them: it is not a step between the others but a layer under five of them,
/// which is why it is listed last.
/// </para>
/// </remarks>
internal static class IndexPhases
{
    /// <summary>Walking the workspace and stat-ing every file.</summary>
    internal const string Scan = "scan";

    /// <summary>Reading what the index already holds, one row per file.</summary>
    internal const string Load = "load";

    /// <summary>Extracting each changed file and writing its rows, one batch per file.</summary>
    internal const string Write = "write";

    /// <summary>Dropping the rows of files the walk no longer sees.</summary>
    internal const string Remove = "remove";

    /// <summary>Resolving relations into edges once every declaration of the build is stored.</summary>
    internal const string Edges = "edges";

    /// <summary>Assigning the graph's communities, which depends on the whole graph rather than one file.</summary>
    /// <remarks>
    /// The five steps below all run inside this one, and each of them is nested: the phase stays the total, and the
    /// steps say what the total is made of (spec 027, D1).
    /// </remarks>
    internal const string Communities = "communities";

    /// <summary>Reading the declarations a community pass clusters, in <c>key</c> order.</summary>
    internal const string Nodes = "nodes";

    /// <summary>Reading the stored relations into one adjacency list per declaration.</summary>
    internal const string Adjacency = "adjacency";

    /// <summary>Partitioning the graph into modularity communities and numbering them.</summary>
    internal const string Louvain = "louvain";

    /// <summary>Computing each declaration's degree centrality over the same graph.</summary>
    internal const string Degree = "degree";

    /// <summary>Writing both columns back, one row per declaration, in one batch.</summary>
    internal const string Assign = "assign";

    /// <summary>Recording the run: its id, its timestamp, whether it finished and what the index now holds.</summary>
    internal const string Meta = "meta";

    /// <summary>The store's own batch commit, which runs inside <see cref="Write"/>, <see cref="Remove"/>, <see cref="Edges"/>, <see cref="Assign"/> and <see cref="Meta"/>.</summary>
    internal const string Commit = "commit";

    /// <summary>Every step, in the order a build runs them, with the nested ones last within their parent.</summary>
    internal static readonly string[] Order =
    [
        Scan,
        Load,
        Write,
        Remove,
        Edges,
        Communities,
        Nodes,
        Adjacency,
        Louvain,
        Degree,
        Assign,
        Meta,
        Commit,
    ];

    /// <summary>The steps whose time is already counted inside a parent step's.</summary>
    /// <remarks>
    /// The store's commit and the clusterer's five steps are the only ones. A nested step is printed as
    /// <c>of which …</c> and takes no share of the run, because a share would count its seconds twice.
    /// </remarks>
    private static readonly string[] NestedSteps =
    [
        Nodes,
        Adjacency,
        Louvain,
        Degree,
        Assign,
        Commit,
    ];

    /// <summary>
    /// Gets a value indicating whether a step's time is already counted inside another step's.
    /// </summary>
    /// <param name="name">The step's name.</param>
    /// <returns><see langword="true"/> for the store's commit and for the five steps inside <see cref="Communities"/>.</returns>
    internal static bool IsNested(string name) => Array.IndexOf(NestedSteps, name) >= 0;
}
