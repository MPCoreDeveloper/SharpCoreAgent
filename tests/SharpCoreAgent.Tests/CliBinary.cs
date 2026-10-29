namespace SharpCoreAgent.Tests;

using Xunit;

/// <summary>
/// The CLI this build produced, which the <c>CopyProductCli</c> target puts beside the test assembly.
/// </summary>
/// <remarks>
/// Two tests need a process that only this binary can be: one starts it as an MCP server, the other hands its command
/// line to <c>mcp bench</c> so the measurement runs against a real server. Neither reaches for a path under <c>src</c>,
/// so both measure the build under test rather than whichever copy happens to be on disk.
/// </remarks>
internal static class CliBinary
{
    /// <summary>Gets the executable's absolute path, asserted to exist once per run.</summary>
    internal static string Executable { get; } = Locate();

    /// <summary>
    /// Names the command line that serves one workspace, which is what <c>mcp config</c> prints and what the bench
    /// passes to <see cref="SharpCoreAgent.Infrastructure.Mcp.McpToolHost"/>.
    /// </summary>
    /// <param name="workspaceRoot">The workspace to serve.</param>
    /// <returns>The quoted command line.</returns>
    internal static string Serve(string workspaceRoot) =>
        $"\"{Executable}\" mcp serve --workspace \"{workspaceRoot}\" --no-refresh";

    private static string Locate()
    {
        var name = OperatingSystem.IsWindows() ? "sharpcoreagent.exe" : "sharpcoreagent";
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Cli", name);

        Assert.True(
            File.Exists(path),
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the CLI was not copied to {path} (see CopyProductCli)"));

        return path;
    }
}
