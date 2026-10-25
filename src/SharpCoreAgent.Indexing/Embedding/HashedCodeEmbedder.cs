using System.Globalization;
using SharpCoreAgent.Indexing.Extraction;

namespace SharpCoreAgent.Indexing.Embedding;

/// <summary>
/// A local embedder that hashes code tokens into a fixed number of dimensions, so a vector costs no network call,
/// no model file and no download.
/// </summary>
/// <remarks>
/// The idea is the hashing trick: take the same code tokens the lexical leg uses, hash each one to a dimension,
/// add its <c>1 + log(tf)</c> weight there, and normalise. Words that co-occur in identifiers land in the same
/// dimensions often enough that two passages about the same thing point in roughly the same direction, which is
/// all the vector leg needs in order to reorder what BM25 already found.
/// <para>
/// What this embedder is not: it has no idea that <c>car</c> and <c>automobile</c> are related. It is a bag of
/// hashed words, and it is the default because it is offline, free, deterministic and testable — the provider
/// embedder behind the same port exists for the day that matters more than those four.
/// </para>
/// <para>
/// The hash is FNV-1a over the token's UTF-16 characters. Hashing characters rather than UTF-8 bytes keeps the
/// whole thing allocation-free (a million-token file allocates nothing beyond its token list), and .NET strings
/// are UTF-16 everywhere, so the vector for a given token is the same number on every platform and every run.
/// </para>
/// </remarks>
internal sealed class HashedCodeEmbedder : ICodeTextEmbedder
{
    /// <summary>The width the index schema declares in its <c>VECTOR(n)</c> columns.</summary>
    internal const int DefaultDimensions = 512;

    private const uint FnvOffsetBasis = 2166136261;

    private const uint FnvPrime = 16777619;

    private readonly string _id;

    /// <summary>
    /// Initialises a new instance of the <see cref="HashedCodeEmbedder"/> class.
    /// </summary>
    /// <param name="dimensions">The width of the vectors; must match the schema's <c>VECTOR(n)</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dimensions"/> is not positive.</exception>
    public HashedCodeEmbedder(int dimensions = DefaultDimensions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);

        Dimensions = dimensions;
        _id = string.Create(CultureInfo.InvariantCulture, $"hashed-local/{dimensions}");
    }

    /// <inheritdoc />
    public string Id => _id;

    /// <inheritdoc />
    public int Dimensions { get; }

    /// <summary>
    /// Creates the default embedder the index uses when nothing else is configured.
    /// </summary>
    /// <returns>A 512-dimension embedder reporting the id <c>hashed-local/512</c>.</returns>
    public static HashedCodeEmbedder CreateDefault() => new();

    /// <inheritdoc />
    public void Embed(string text, Span<float> destination)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (destination.Length != Dimensions)
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A {Id} vector is {Dimensions} wide, but the destination is {destination.Length}."),
                nameof(destination));
        }

        destination.Clear();

        var tokens = CodeTokenizer.Split(text);
        if (tokens.Count == 0)
        {
            // No words is a real answer: the zero vector ranks nothing, and a passage of punctuation must not be
            // able to throw and fail a whole build.
            return;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            counts[token] = counts.TryGetValue(token, out var count) ? count + 1 : 1;
        }

        // Accumulate in ordinal token order, not insertion order: floating-point addition is not associative, so
        // the same bag of words in a different sequence would otherwise produce a vector that differs in the last
        // bits, and "the same passage vectors the same" is a property the incremental build relies on.
        var ordered = new List<string>(counts.Keys);
        ordered.Sort(StringComparer.Ordinal);

        foreach (var token in ordered)
        {
            var hash = Hash(token);
            var dimension = (int)(hash % (uint)Dimensions);

            // Feature hashing collides different words into the same dimension; letting some of them count
            // negatively keeps the collisions from adding up into a bias toward one direction.
            var weight = 1f + MathF.Log(counts[token]);
            destination[dimension] += (hash & 0x8000_0000) == 0 ? weight : -weight;
        }

        Normalise(destination);
    }

    /// <summary>
    /// Measures how much two vectors have in common, which is all the ranking needs to know.
    /// </summary>
    /// <param name="left">The first vector.</param>
    /// <param name="right">The second vector, which must be the same width.</param>
    /// <returns>The cosine similarity, in <c>[-1, 1]</c>; zero when either vector is the zero vector.</returns>
    /// <exception cref="ArgumentException">The vectors differ in width.</exception>
    internal static float CosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Cosine similarity needs two vectors of the same width.", nameof(right));
        }

        var dot = 0f;
        var leftNorm = 0f;
        var rightNorm = 0f;

        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }

        if (leftNorm <= 0f || rightNorm <= 0f)
        {
            return 0f;
        }

        return dot / (MathF.Sqrt(leftNorm) * MathF.Sqrt(rightNorm));
    }

    private static uint Hash(string token)
    {
        var hash = FnvOffsetBasis;

        foreach (var character in token)
        {
            hash ^= character;
            hash *= FnvPrime;
        }

        return hash;
    }

    private static void Normalise(Span<float> vector)
    {
        var sumOfSquares = 0f;
        foreach (var component in vector)
        {
            sumOfSquares += component * component;
        }

        if (sumOfSquares <= 0f)
        {
            return;
        }

        var inverse = 1f / MathF.Sqrt(sumOfSquares);
        for (var index = 0; index < vector.Length; index++)
        {
            vector[index] *= inverse;
        }
    }

}
