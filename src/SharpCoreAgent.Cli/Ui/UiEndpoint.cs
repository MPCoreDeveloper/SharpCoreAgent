using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Where the panel is published: the loopback address, the port, and the one URL the listener answers on.
/// </summary>
/// <remarks>
/// The prefix and the printed URL are deliberately the same string. A listener whose prefix and whose advertised address
/// differed by a name — <c>localhost</c> in one, <c>127.0.0.1</c> in the other — would 403 the developer who retyped the
/// URL, and the <c>Host</c> check of FR-004 is exactly that comparison, so there is one string and no spelling to get
/// wrong. The port is resolved before this type exists: a prefix whose port is 0 is refused by the platform (measured,
/// plan D5), so "choose a port" happens first and this type only ever holds a real one.
/// </remarks>
internal sealed record UiEndpoint
{
    /// <summary>The loopback address the panel binds, which is not a name and cannot be configured.</summary>
    /// <remarks>
    /// <c>127.0.0.1</c> and nothing else: not <c>0.0.0.0</c>, not <c>::</c>, not the machine's name. The page carries a
    /// workspace path and counts, which is a map of a private repository, and D2 answers the "why can I not publish it"
    /// question once rather than per flag.
    /// </remarks>
    internal const string LoopbackHost = "127.0.0.1";

    /// <summary>The highest port a listener can be given.</summary>
    internal const int HighestPort = 65535;

    private UiEndpoint(int port)
    {
        Port = port;
    }

    /// <summary>Gets the resolved port, which is never 0.</summary>
    internal int Port { get; }

    /// <summary>
    /// Gets the URL the panel is published at, which is also the listener's prefix.
    /// </summary>
    /// <returns>For example <c>http://127.0.0.1:51932/</c>.</returns>
#pragma warning disable S5332 // Loopback only: the address is 127.0.0.1, so there is no network to encrypt and no certificate a browser would trust.
    internal string Url => string.Create(CultureInfo.InvariantCulture, $"http://{LoopbackHost}:{Port}/");
#pragma warning restore S5332

    /// <summary>
    /// Gets the value a request's <c>Host</c> header must carry to be answered.
    /// </summary>
    /// <returns>For example <c>127.0.0.1:51932</c>.</returns>
    /// <remarks>
    /// The header holds the authority without a scheme, so the comparison is against this exact string rather than
    /// against the URL trimmed: one string comparison, and no case folding that could let <c>LOCALHOST</c> through to a
    /// listener that never bound it.
    /// </remarks>
    internal string Authority => string.Create(CultureInfo.InvariantCulture, $"{LoopbackHost}:{Port}");

    /// <summary>
    /// Creates the endpoint for a port that has already been chosen.
    /// </summary>
    /// <param name="port">A port from 1 to 65535; 0 is not an endpoint.</param>
    /// <returns>The endpoint.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The port is outside that range.</exception>
    internal static UiEndpoint Loopback(int port)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, HighestPort);
        return new UiEndpoint(port);
    }

    /// <summary>
    /// Parses the value of <c>--port</c>.
    /// </summary>
    /// <param name="value">The text the developer typed.</param>
    /// <returns>The port, where 0 means "one chosen at bind time".</returns>
    /// <exception cref="CliUsageException">The text is not a whole number in <c>0–65535</c>.</exception>
    /// <remarks>
    /// Digits only, so <c>+80</c>, <c> 80</c> and <c>0x50</c> are refused rather than half understood, and the range is
    /// checked here rather than at the bind so <c>--port 70000</c> is a usage error before anything is opened. A port the
    /// platform will not give up — 80 is access-denied even to a user with no URL ACL to reserve — is not a parse error:
    /// it is refused at the bind, where the message can name the port and say what happened.
    /// </remarks>
    internal static int ParsePort(string? value)
    {
        if (value is not null
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 0 and <= HighestPort)
        {
            return port;
        }

        throw new CliUsageException(
            $"option '--port' needs a whole number from 0 to {HighestPort}, but got '{value}'.",
            "Use --port 0 for any free port, or a number from 1 to 65535.");
    }

    /// <summary>
    /// Chooses a free loopback port for <c>--port 0</c>.
    /// </summary>
    /// <returns>A port nothing was listening on at the moment it was read.</returns>
    /// <exception cref="CliUsageException">No loopback port could be chosen.</exception>
    /// <remarks>
    /// A <see cref="TcpListener"/> on port 0 asks the operating system for an ephemeral port, which is the only way to get
    /// one: <c>http.sys</c> refuses a prefix whose port is 0 outright. The socket is closed before the port is returned,
    /// so there is a window in which another process could take it — the window <c>--port 0</c> retries over, and the
    /// reason an explicit port never retries. Asking through a socket rather than the listener is deliberate: it spends
    /// no URL registration, so a port that turns out to be taken costs nothing.
    /// </remarks>
    internal static int ChoosePort()
    {
        var picker = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            picker.Start();
            return ((IPEndPoint)picker.LocalEndpoint).Port;
        }
        catch (SocketException ex)
        {
            throw new CliUsageException(
                $"no free loopback port could be found: {ex.Message}",
                "Name one with --port, for example --port 8080.");
        }
        finally
        {
            picker.Stop();
        }
    }

    /// <summary>
    /// Builds the object <c>ui --serve --json</c> prints before the first request.
    /// </summary>
    /// <param name="workspaceRoot">The workspace the index belongs to.</param>
    /// <param name="refresh">Whether the panel walks the tree before each answer.</param>
    /// <returns>One line of JSON, without a trailing newline.</returns>
    /// <remarks>
    /// The shape is FR-008's, and it is printed rather than described because a script that starts the panel needs the
    /// port: scraping it out of a sentence is the kind of parsing that breaks when a word changes. Everything else the
    /// command says — the note about how to open the page — goes to stderr, so this line stays parseable.
    /// </remarks>
    internal string ToJson(string workspaceRoot, bool refresh)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["command"] = "ui",
            ["action"] = "serve",
            ["url"] = Url,
            ["workspace"] = IndexStatusDocument.Slash(workspaceRoot),
            ["refresh"] = refresh,
        });
    }
}
