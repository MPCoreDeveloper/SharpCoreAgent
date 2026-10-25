using System.Security.Cryptography;
using System.Text;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// One file's bytes, as the index needs them: a content hash and the text every later stage reads.
/// </summary>
/// <remarks>
/// The hash is taken over the bytes on disk, not over the decoded text, so it answers "is this the same file?"
/// independently of how the text happened to be decoded. That is the comparison the incremental build makes
/// before it decides a file is unchanged, and it is why a rewrite that leaves size and timestamp identical is
/// still caught.
/// <para>
/// Decoding is UTF-8 with byte-order-mark detection. A UTF-16 or binary file never reaches this point because
/// <see cref="Extraction.WorkspaceFileScanner"/> marks it skipped, so the one encoding here is the one every
/// indexable file actually uses.
/// </para>
/// </remarks>
internal sealed record IndexFileContent
{
    /// <summary>Gets the lowercase hexadecimal SHA-256 of the file's bytes.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Gets the file's text, with any byte-order mark removed.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// Reads and hashes one file.
    /// </summary>
    /// <param name="fullPath">The absolute path to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The hash and text.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file is not readable.</exception>
    public static async ValueTask<IndexFileContent> ReadAsync(string fullPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);

        // ReadAllBytesAsync rather than ReadAllTextAsync: the hash has to cover the bytes, and decoding the
        // buffer a second time costs less than opening the file twice.
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        return new IndexFileContent
        {
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Text = text,
        };
    }

    /// <summary>
    /// Hashes one passage's text the way <c>chunks.sha256</c> stores it.
    /// </summary>
    /// <param name="text">The passage text.</param>
    /// <returns>Lowercase hexadecimal SHA-256.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static string HashText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
