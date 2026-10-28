using System.Net;
using System.Net.Sockets;

namespace SharpCoreAgent.Infrastructure.Web;

/// <summary>
/// Decides whether a host may be fetched, from the addresses it resolves to.
/// </summary>
/// <remarks>
/// The rule is a refusal list, not an allow list: everything on the public internet is readable, and everything that
/// is this machine, its private network, its link, or its cloud control plane is not. That asymmetry is the point —
/// an agent that can read <c>http://169.254.169.254/</c> can read instance credentials, and one that can read
/// <c>http://127.0.0.1:8080/</c> can drive whatever the developer happens to be running.
/// <para>
/// The developer's own hosts are exempt through <see cref="IsExempt"/>, which is how a page served from
/// <c>localhost</c> is fetched after all: authority is granted by naming the host, once, on the command line.
/// </para>
/// <para>
/// Everything here is a pure function of an address, so the suite can assert every category without a socket.
/// </para>
/// </remarks>
public static class WebAddressPolicy
{
    /// <summary>Gets the address every cloud instance serves its credentials from.</summary>
#pragma warning disable S1313 // The cloud metadata endpoints are exactly the addresses this refusal list exists for.
    public static IPAddress MetadataEndpoint { get; } = IPAddress.Parse("169.254.169.254");

    /// <summary>Gets the address the same service answers on inside an IPv6-only virtual network.</summary>
    public static IPAddress MetadataEndpointV6 { get; } = IPAddress.Parse("fd00:ec2::254");
#pragma warning restore S1313

    /// <summary>
    /// Names what an address is, when it is one the tool refuses.
    /// </summary>
    /// <param name="address">The resolved address.</param>
    /// <returns>A short category such as <c>loopback</c>, or <see langword="null"/> when the address is public.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is null.</exception>
    public static string? Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // A v4-mapped v6 address reaches the same place as the v4 original, so it is judged as the v4 original:
        // otherwise ::ffff:127.0.0.1 would be a way through the loopback refusal.
        var mapped = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        if (mapped.Equals(MetadataEndpoint) || address.Equals(MetadataEndpointV6))
        {
            return "cloud metadata";
        }

        var bytes = mapped.GetAddressBytes();
        if (mapped.AddressFamily == AddressFamily.InterNetwork)
        {
            return ClassifyV4(bytes);
        }

        if (IPAddress.IsLoopback(mapped))
        {
            return "loopback";
        }

        if (mapped.IsIPv6Multicast)
        {
            return "multicast";
        }

        if (bytes.All(b => b == 0))
        {
            return "unspecified";
        }

        if ((bytes[0] & 0xfe) == 0xfc)
        {
            return "unique-local";
        }

        return bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80 ? "link-local" : null;
    }

    /// <summary>
    /// Determines whether a host was named by the developer, and is therefore exempt from the address refusal.
    /// </summary>
    /// <param name="host">The host as written in the URL, without brackets.</param>
    /// <param name="port">The port the URL asks for.</param>
    /// <param name="addresses">The addresses the host resolved to.</param>
    /// <param name="allowedHosts">The entries the developer passed to <c>--web-allow</c>.</param>
    /// <returns><see langword="true"/> when one entry names this host or one of its addresses.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <remarks>
    /// An entry may be a host, a <c>host:port</c>, a <c>[v6]:port</c>, or a bare IP literal. Matching is exact and
    /// case-insensitive: there are no wildcards, because <c>*.internal</c> is a rule nobody can review while three
    /// named hosts can be read at a glance. A <c>host:port</c> entry matches that port only, so naming a development
    /// server does not also open every other port on the loopback interface.
    /// </remarks>
    public static bool IsExempt(
        string host,
        int port,
        IReadOnlyList<IPAddress> addresses,
        IReadOnlyList<string> allowedHosts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(allowedHosts);

        foreach (var entry in allowedHosts)
        {
            if (!TrySplitEntry(entry, out var name, out var entryPort))
            {
                continue;
            }

            if (entryPort is { } namedPort && namedPort != port)
            {
                continue;
            }

            if (string.Equals(name, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (IPAddress.TryParse(name, out var literal))
            {
                var wanted = Normalize(literal);
                if (addresses.Any(address => Normalize(address).Equals(wanted)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static string? ClassifyV4(byte[] bytes)
    {
        if (bytes[0] == 127)
        {
            return "loopback";
        }

        if (bytes[0] == 0)
        {
            return "unspecified";
        }

        if (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || (bytes[0] == 192 && bytes[1] == 168))
        {
            return "private";
        }

        if (bytes[0] == 169 && bytes[1] == 254)
        {
            return "link-local";
        }

        if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
        {
            return "carrier-grade NAT";
        }

        if (bytes[0] >= 224 && bytes[0] <= 239)
        {
            return "multicast";
        }

        return bytes[0] >= 240 ? "reserved" : null;
    }

    private static bool TrySplitEntry(string? entry, out string host, out int? port)
    {
        host = string.Empty;
        port = null;

        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var text = entry.Trim();

        // [v6]:port and [v6] are the two bracketed forms a URL itself would accept.
        if (text[0] == '[')
        {
            var close = text.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                return false;
            }

            host = text[1..close];
            var rest = text[(close + 1)..];
            return rest.Length == 0 || TryPort(rest, out port);
        }

        // Exactly one colon is host:port; an unbracketed v6 literal has more, and is taken whole.
        var colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && TryPort(text[colon..], out var parsed))
        {
            host = text[..colon];
            port = parsed;
            return host.Length > 0;
        }

        host = text;
        return true;
    }

    private static bool TryPort(string text, out int? port)
    {
        port = null;
        if (text.Length < 2 || text[0] != ':')
        {
            return false;
        }

        if (!int.TryParse(
                text[1..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value) || value is < 1 or > 65535)
        {
            return false;
        }

        port = value;
        return true;
    }
}
