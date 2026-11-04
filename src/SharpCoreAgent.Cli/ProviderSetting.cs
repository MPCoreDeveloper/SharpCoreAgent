using SharpCoreAgent.Infrastructure.Providers;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Parses a provider-scoped settings key such as <c>providers.grok.model</c>.
/// </summary>
/// <remarks>
/// Only settings that are meaningful per backend are accepted: the model and the endpoint. A budget, an approval mode or
/// a credential file belongs to the session, not to one provider, and accepting it here would make two active values
/// whose precedence nobody can explain.
/// </remarks>
internal static class ProviderSetting
{
    private const string Prefix = "providers.";

    private static readonly string[] Allowed = ["model", "base-url"];

    /// <summary>Parses a provider-scoped key.</summary>
    /// <param name="name">The key as written.</param>
    /// <param name="provider">The canonical provider key when parsing succeeds.</param>
    /// <param name="setting">The canonical per-provider setting when parsing succeeds.</param>
    /// <returns><see langword="true"/> when the key names a known provider and an allowed setting.</returns>
    public static bool TryParse(string? name, out string provider, out string setting)
    {
        provider = string.Empty;
        setting = string.Empty;

        if (string.IsNullOrWhiteSpace(name)
            || !name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = name.Split('.');
        if (parts.Length != 3
            || !string.Equals(parts[0], "providers", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var known = ModelCatalog.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate, parts[1].Trim(), StringComparison.OrdinalIgnoreCase));
        var key = parts[2].Trim().ToLowerInvariant();
        if (known is null || !Allowed.Contains(key, StringComparer.Ordinal))
        {
            return false;
        }

        provider = known;
        setting = key;
        return true;
    }

    /// <summary>Builds the canonical spelling of a provider-scoped key.</summary>
    /// <param name="provider">A known provider key.</param>
    /// <param name="setting">An allowed setting.</param>
    /// <returns>The key written in the settings file.</returns>
    public static string Canonical(string provider, string setting) => $"{Prefix}{provider}.{setting}";
}
