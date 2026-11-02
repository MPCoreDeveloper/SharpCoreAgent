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
/// <see cref="Commit"/> is the exception: it is not a step between the others but a layer under four of them, which
/// is why it is listed last and marked nested instead of being interleaved with the ones that contain it.
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
    internal const string Communities = "communities";

    /// <summary>Recording the run: its id, its timestamp, whether it finished and what the index now holds.</summary>
    internal const string Meta = "meta";

    /// <summary>The store's own batch commit, which runs inside <see cref="Write"/>, <see cref="Remove"/>, <see cref="Edges"/> and <see cref="Meta"/>.</summary>
    internal const string Commit = "commit";

    /// <summary>Every step, in the order a build runs them, with the nested one last.</summary>
    internal static readonly string[] Order =
    [
        Scan,
        Load,
        Write,
        Remove,
        Edges,
        Communities,
        Meta,
        Commit,
    ];

    /// <summary>
    /// Gets a value indicating whether a step's time is already counted inside another step's.
    /// </summary>
    /// <param name="name">The step's name.</param>
    /// <returns><see langword="true"/> for the store's commit.</returns>
    internal static bool IsNested(string name) => string.Equals(name, Commit, StringComparison.Ordinal);
}
