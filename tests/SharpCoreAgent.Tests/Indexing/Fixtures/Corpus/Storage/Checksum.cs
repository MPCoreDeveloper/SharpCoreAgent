using System.Security.Cryptography;

namespace Demo.Storage;

/// Hashes a stream with SHA-256 and spells the digest as lower-case hex.
public static class Checksum
{
    public static string Of(Stream content)
    {
        var digest = SHA256.HashData(content);
        return Convert.ToHexStringLower(digest);
    }

    public static bool Matches(Stream content, string expected) =>
        string.Equals(Of(content), expected, StringComparison.OrdinalIgnoreCase);
}
