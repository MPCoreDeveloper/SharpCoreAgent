namespace SharpCoreAgent.Indexing.Embedding;

/// <summary>
/// Turns passage text into a fixed-width vector, so the index can answer a question that shares no words with
/// the code.
/// </summary>
/// <remarks>
/// This is a port, in the strict sense: nothing outside <c>SharpCoreAgent.Indexing</c> may appear in its signature, and
/// no implementation may reach the network unless it is explicitly the provider-backed one. The default
/// implementation is local and deterministic, which is what makes a ranking test reproducible.
/// <para>
/// <see cref="Id"/> carries the dimension count because it is stored in <c>meta['embedder']</c> and compared on
/// every open: a vector written by a 512-dimension embedder must never be compared against a 1536-dimension one,
/// and the id is how the index knows without being told.
/// </para>
/// </remarks>
internal interface ICodeTextEmbedder
{
    /// <summary>
    /// Gets the identity of the embedder, dimensions included (for example <c>hashed-local/512</c>).
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the width of every vector this embedder produces.
    /// </summary>
    int Dimensions { get; }

    /// <summary>
    /// Embeds one piece of text.
    /// </summary>
    /// <param name="text">The text; empty text is legal and produces the zero vector.</param>
    /// <param name="destination">
    /// Where the vector goes; it is overwritten in full and must be exactly <see cref="Dimensions"/> long.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not <see cref="Dimensions"/> long.</exception>
    void Embed(string text, Span<float> destination);
}
