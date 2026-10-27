using System.Globalization;
using System.Security.Cryptography;

namespace Demo.Auth;

/// Hashes a password with PBKDF2, storing the salt and the iteration count beside the digest.
public static class PasswordHasher
{
    private const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var digest = Derive(password, salt, Iterations);
        return string.Create(CultureInfo.InvariantCulture, $"pbkdf2-sha512${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(digest)}");
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        var iterations = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var salt = Convert.FromBase64String(parts[2]);
        return CryptographicOperations.FixedTimeEquals(Derive(password, salt, iterations), Convert.FromBase64String(parts[3]));
    }

    private static byte[] Derive(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, 32);
}
