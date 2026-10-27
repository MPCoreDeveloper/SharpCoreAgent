namespace Demo.Http;

/// Reads Accept-Language and picks the best supported language for content negotiation.
public static class HeaderParser
{
    public static string Preferred(string? acceptLanguage, params string[] supported)
    {
        if (string.IsNullOrWhiteSpace(acceptLanguage))
        {
            return supported[0];
        }

        foreach (var part in acceptLanguage.Split(','))
        {
            var tag = part.Split(';')[0].Trim();
            foreach (var candidate in supported)
            {
                if (string.Equals(candidate, tag, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return supported[0];
    }
}
