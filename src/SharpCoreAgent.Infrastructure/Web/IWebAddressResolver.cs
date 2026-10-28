using System.Net;
using System.Net.Sockets;

namespace SharpCoreAgent.Infrastructure.Web;

/// <summary>
/// Turns a host name into the addresses it stands for.
/// </summary>
/// <remarks>
/// An interface for one reason: the safety decision is made from addresses, and a test that had to reach DNS would be a
/// test that fails on a train. The suite supplies its own resolver, so every refusal is asserted without a socket.
/// </remarks>
public interface IWebAddressResolver
{
    /// <summary>
    /// Resolves a host.
    /// </summary>
    /// <param name="host">The host name, or an IP literal.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>Every address the host stands for, in the resolver's order.</returns>
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>
/// The resolver a real process uses: the platform's DNS, which also answers an IP literal with itself.
/// </summary>
/// <remarks>
/// A name that resolves to several addresses produces all of them, and the tool refuses the host when <em>any</em> of
/// them is blocked. Taking the first answer would be enough to reach the machine; requiring all of them to be public
/// is the only rule a hostile DNS answer cannot step around.
/// </remarks>
public sealed class DnsWebAddressResolver : IWebAddressResolver
{
    /// <summary>Gets the shared instance.</summary>
    public static DnsWebAddressResolver Instance { get; } = new();

    private DnsWebAddressResolver()
    {
    }

    /// <inheritdoc />
    /// <exception cref="SocketException">The host does not resolve.</exception>
    /// <exception cref="ArgumentException"><paramref name="host"/> is blank.</exception>
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (IPAddress.TryParse(host, out var literal))
        {
            return [literal];
        }

        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        return addresses;
    }
}
