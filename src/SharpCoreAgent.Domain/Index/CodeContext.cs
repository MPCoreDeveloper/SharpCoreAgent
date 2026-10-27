namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// Everything one <c>code_context</c> answer needs: the declaration at the centre, what surrounds it and why.
/// </summary>
/// <remarks>
/// The tool could have re-derived the centre from the neighbours, but a list of neighbours does not say which
/// declaration they are neighbours <em>of</em>, and a caller reading the answer needs that sentence. Returning the
/// centre, its own passage and the full neighbour count alongside the blocks is what lets the tool print
/// <c>calls … · w 0.83</c> under the right heading instead of guessing.
/// <para>
/// <see cref="Declarations"/> is filled only when the target was a file path: naming a file asks "what is in here?",
/// and the declarations inside it are the first half of the answer. A symbol target leaves it empty, because the
/// question was about one declaration.
/// </para>
/// </remarks>
public sealed record CodeContext
{
    /// <summary>Gets the target exactly as the caller named it, trimmed.</summary>
    public required string Target { get; init; }

    /// <summary>
    /// Gets the declaration at the centre of the neighbourhood, or <see langword="null"/> when the target names
    /// nothing the index holds.
    /// </summary>
    public CodeSymbolRef? Subject { get; init; }

    /// <summary>
    /// Gets the declarations of the subject's file, in line order, when the target was a path; empty otherwise.
    /// </summary>
    public IReadOnlyList<CodeSymbolRef> Declarations { get; init; } = [];

    /// <summary>
    /// Gets the passage that holds the subject's declaration, or <see langword="null"/> when no passage covers it.
    /// </summary>
    public CodeEvidence? Focus { get; init; }

    /// <summary>Gets the neighbouring blocks, strongest first; possibly empty.</summary>
    public IReadOnlyList<CodeEvidence> Neighbours { get; init; } = [];

    /// <summary>
    /// Gets how many neighbours the walk reached before the caller's <c>k</c> was applied, so an answer can say
    /// "8 shown of 26" instead of pretending eight is all there is.
    /// </summary>
    public int Considered { get; init; }

    /// <summary>
    /// Gets the smallest edge weight the walk accepted, or <c>0</c> when no walk ran.
    /// </summary>
    /// <remarks>
    /// The answer prints this floor beside the neighbour count, because "8 shown of 26" invites the question of
    /// what happened to the other eighteen. Naming the threshold answers it without listing edges the walk judged
    /// too weak to be evidence.
    /// </remarks>
    public double MinWeight { get; init; }

    /// <summary>
    /// Creates the answer for a target the index cannot resolve.
    /// </summary>
    /// <param name="target">The target as the caller named it.</param>
    /// <returns>A context with no subject and no neighbours.</returns>
    public static CodeContext Empty(string target) => new()
    {
        Target = string.IsNullOrWhiteSpace(target) ? string.Empty : target.Trim(),
    };
}
