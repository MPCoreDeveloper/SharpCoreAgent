using System.Net;
using System.Net.Sockets;

namespace SharpCoreAgent.Infrastructure.Web;

/// <summary>
/// Builds the handler <c>fetch_url</c> sends through, and makes the address judgement again at connect time.
/// </summary>
/// <remarks>
/// The tool already judges every hop before it requests it, which is enough for a URL and not enough for a connection:
/// a name can resolve to a public address for the check and to <c>127.0.0.1</c> for the socket. This handler closes
/// that window by resolving a second time inside <see cref="SocketsHttpHandler.ConnectCallback"/> and refusing the
/// address the socket is about to be opened to, so the address that is judged is the address that is used.
/// <para>
/// It also refuses a proxy, by construction: through a proxy the callback is handed the proxy's endpoint, and the
/// proxy then resolves the name itself — which turns every refusal in <see cref="WebAddressPolicy"/> into advice. A
/// fetch that cannot be made directly is a fetch this tool does not make, and the failure says so rather than
/// silently leaving the machine through something the developer never named on the command line.
/// </para>
/// </remarks>
public static class WebFetchHandler
{
    /// <summary>
    /// Creates the handler the fetch client uses.
    /// </summary>
    /// <param name="resolver">The resolver the check consults, shared with the tool so both see one DNS.</param>
    /// <param name="options">The ceilings and the hosts the developer named.</param>
    /// <returns>A handler with no redirect, no cookie jar, no proxy and a connect-time address check.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static SocketsHttpHandler Create(IWebAddressResolver resolver, WebFetchOptions options)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(options);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = (context, cancellationToken) => ConnectAsync(context, resolver, options, cancellationToken),
        };
    }

    /// <summary>
    /// Resolves the endpoint once more, refuses a blocked address, and connects to a permitted one.
    /// </summary>
    /// <param name="context">The endpoint the handler is about to open a socket to.</param>
    /// <param name="resolver">The resolver the check consults.</param>
    /// <param name="options">The hosts the developer named.</param>
    /// <param name="cancellationToken">Cancels the lookup and the connect.</param>
    /// <returns>An open stream to the first address that accepted.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="HttpRequestException">
    /// The name resolves to a blocked address, or no address accepted the connection. The tool reports it as a failed
    /// result naming the address, so a refusal reads like a refusal rather than like a network fault.
    /// </exception>
    public static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        IWebAddressResolver resolver,
        WebFetchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(options);

        var host = context.DnsEndPoint.Host;
        var port = context.DnsEndPoint.Port;
        var addresses = await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);

        var blocked = addresses.Select(WebAddressPolicy.Classify).FirstOrDefault(category => category is not null);
        if (blocked is not null && !WebAddressPolicy.IsExempt(host, port, addresses, options.AllowedHosts))
        {
            throw new HttpRequestException(
                $"refused at connect time: {host}:{port} resolves to {string.Join(", ", addresses)}, which is {blocked}. " +
                $"The address of a hop can change between the check and the socket, so this one is judged again here. " +
                $"If this host is meant, name it for this invocation: --web-allow {host}");
        }

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException ex)
            {
                // One address of several: the rest are still worth trying, and the last failure is what gets reported.
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException($"no address of {host}:{port} accepted a connection.", last);
    }
}
