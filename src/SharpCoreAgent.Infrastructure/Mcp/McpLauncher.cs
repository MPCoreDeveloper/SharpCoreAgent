using System.Globalization;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// Turns the command line a developer typed into something the platform can start.
/// </summary>
/// <remarks>
/// The command runs through the platform shell, and that is a decision rather than a shortcut: the
/// headline servers are distributed as <c>npx</c>, <c>uvx</c> and <c>deno</c> shims, which on Windows are
/// <c>.cmd</c> scripts that <see cref="System.Diagnostics.Process"/> will not start directly and on Unix
/// are scripts whose interpreter the kernel resolves. Running the shell a developer would have used means
/// their command works unchanged, quoting and all.
/// <para>
/// The consequence is that the child is the shell, so killing it has to kill its tree — which is what
/// <see cref="McpServerConnection.DisposeAsync"/> does.
/// </para>
/// </remarks>
internal static class McpLauncher
{
    /// <summary>
    /// Names the executable and arguments that run one server command line.
    /// </summary>
    /// <param name="commandLine">The command line exactly as the developer typed it.</param>
    /// <returns>
    /// The file to start, its argv-style arguments, and — on Windows only — a raw argument line that must be used
    /// instead of them.
    /// </returns>
    /// <remarks>
    /// The two platforms need different shapes, and the reason is worth keeping in view. On Windows the command is
    /// handed to <c>cmd</c> as one quoted string, so the line is built by hand: passing it as a single argv element
    /// would escape the developer's own quotes into <c>\"…\"</c>, which <c>cmd</c> then tries to execute as a path.
    /// On Unix the script is one argv element to <c>sh -c</c> and the kernel passes it through verbatim, so the
    /// argv form is not only sufficient but the only safe one.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="commandLine"/> is blank.</exception>
    public static (string FileName, string[] Arguments, string? RawArguments) For(string commandLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        var command = commandLine.Trim();
        return OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/d", "/s", "/c"], string.Create(CultureInfo.InvariantCulture, $"/d /s /c \"{command}\""))
            : ("/bin/sh", ["-c", command], null);
    }

    /// <summary>
    /// Reads the executable name out of a command line, for naming the server's tools.
    /// </summary>
    /// <param name="commandLine">The command line exactly as the developer typed it.</param>
    /// <returns>
    /// The first token's file name without its extension — <c>npx</c> for <c>npx -y pkg</c>, and
    /// <c>mcp-server</c> for <c>"C:\tools\mcp-server.cmd" --flag</c>.
    /// </returns>
    public static string Stem(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        var text = commandLine.Trim();
        string token;

        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            token = close > 0 ? text[1..close] : text[1..];
        }
        else
        {
            var space = text.IndexOf(' ', StringComparison.Ordinal);
            token = space > 0 ? text[..space] : text;
        }

        var name = Path.GetFileNameWithoutExtension(token);
        return string.IsNullOrWhiteSpace(name) ? token : name;
    }
}
