using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SharpCoreAgent.Indexing;

/// <summary>
/// Identifies one build with a ULID: 26 characters that sort by time and carry their own randomness.
/// </summary>
/// <remarks>
/// The build id is how a reader tells "the rows I loaded" from "the rows that are there now": it changes once
/// per build and is written to <c>meta</c> last, so an interrupted build leaves the previous id in place and a
/// truncated one is detectable. A ULID rather than a GUID because sorting 26 fixed-width characters is free
/// and because a human reading two ids in a log can see which came first.
/// </remarks>
internal static class IndexBuildId
{
    private const int Length = 26;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Creates an id for the current moment.</summary>
    /// <returns>A 26-character Crockford base32 string.</returns>
    public static string NewId() => NewId(DateTimeOffset.UtcNow, RandomNumberGenerator.GetBytes(10));

    /// <summary>
    /// Creates an id from a timestamp and explicit randomness, which is what a test needs to be repeatable.
    /// </summary>
    /// <param name="moment">The moment the build started.</param>
    /// <param name="randomness">At least ten bytes; only the first ten are used.</param>
    /// <returns>A 26-character Crockford base32 string.</returns>
    public static string NewId(DateTimeOffset moment, ReadOnlySpan<byte> randomness)
    {
        if (randomness.Length < 10)
        {
            throw new ArgumentException("A ULID needs ten bytes of randomness.", nameof(randomness));
        }

        var chars = new char[Length];

        var timestamp = moment.ToUnixTimeMilliseconds();
        for (var index = 9; index >= 0; index--)
        {
            chars[index] = Alphabet[(int)(timestamp & 0x1F)];
            timestamp >>= 5;
        }

        var high = BinaryPrimitives.ReadUInt64BigEndian(randomness);
        var low = BinaryPrimitives.ReadUInt16BigEndian(randomness[8..]);
        var value = ((UInt128)high << 16) | low;

        for (var index = Length - 1; index >= 10; index--)
        {
            chars[index] = Alphabet[(int)(value & 0x1F)];
            value >>= 5;
        }

        return new string(chars);
    }
}
