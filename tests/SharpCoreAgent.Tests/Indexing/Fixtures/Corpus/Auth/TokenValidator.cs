using System.Security.Claims;

namespace Demo.Auth;

/// Verifies an access token's signature and expiry before any claim is trusted.
public sealed class TokenValidator(IKeyRing keys)
{
    public bool TryValidate(string token, DateTimeOffset nowUtc, out ClaimsPrincipal? principal)
    {
        principal = null;

        var parts = token.Split('.');
        if (parts.Length != 3 || !keys.Verify(parts[0] + '.' + parts[1], parts[2]))
        {
            return false;
        }

        if (JwtPayload.ReadExpiry(parts[1]) <= nowUtc)
        {
            return false;
        }

        principal = new ClaimsPrincipal(new ClaimsIdentity(JwtPayload.ReadClaims(parts[1]), "bearer"));
        return true;
    }
}
