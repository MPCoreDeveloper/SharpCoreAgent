using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The three promises only a real child process can keep: the launch path works on this platform, a server's stderr
/// never reaches stdout, and disposing the connection kills the process tree rather than merely closing its input.
/// </summary>
/// <remarks>
/// The server these tests launch lives in this repository, under <c>tests/SharpCoreAgent.TestMcpServer</c>, and is
/// copied next to this assembly by the test project's build. It is a real MCP server over stdio, small enough to
/// read in one sitting.
/// </remarks>
public sealed class McpProcessTests
{
    private static readonly JsonDocument NoArguments = JsonDocument.Parse("{}");

    [Fact]
    public async Task TheRealServerStartsAndAnswersACall()
    {
        await using var connection = Connect();
        await connection.StartAsync(Token);

        Assert.Equal(["echo", "fail"], connection.Tools.Select(tool => tool.Name));

        var echoed = await connection.CallAsync("echo", Arguments("""{"text":"hello from the test"}"""), TimeSpan.FromSeconds(30), Token);
        Assert.False(echoed.IsError);
        Assert.Equal("hello from the test", echoed.Text);

        var failed = await connection.CallAsync("fail", NoArguments.RootElement.Clone(), TimeSpan.FromSeconds(30), Token);
        Assert.True(failed.IsError);
        Assert.Contains("always fails", failed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StderrIsKeptApartFromTheConversation()
    {
        await using var connection = Connect();
        await connection.StartAsync(Token);

        // The server writes two lines to stderr before it answers anything. A client that merged the streams would
        // have failed its handshake on the first of them, so the assertions below are the second half of the proof.
        Assert.Contains(connection.Stderr, line => line.StartsWith("pid=", StringComparison.Ordinal));
        Assert.Contains("test-mcp-server ready", connection.Stderr);
        Assert.NotEmpty(connection.Tools);
    }

    [Fact]
    public async Task DisposingTheConnectionKillsTheServer()
    {
        var connection = Connect();
        await connection.StartAsync(Token);
        var pid = ServerPid(connection);

        await connection.DisposeAsync();

        Assert.True(await WaitsForExitAsync(pid), string.Create(
            CultureInfo.InvariantCulture,
            $"the server (pid {pid}) was still running after the connection was disposed"));
    }

    [Fact]
    public async Task ACommandThatCannotRunIsAStartupFailureNamingIt()
    {
        await using var connection = new McpServerConnection(
            "missing",
            "sharpcoreagent-no-such-binary --flag",
            new McpClientOptions { ControlTimeout = TimeSpan.FromSeconds(10) });

        var error = await Assert.ThrowsAsync<McpStartupException>(() => connection.StartAsync(Token));

        Assert.Contains("sharpcoreagent-no-such-binary", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheHostStartsOneServerPerFlagAndNamesThemApart()
    {
        var host = new McpToolHost([CommandLine(), CommandLine()], new McpClientOptions());
        try
        {
            await host.StartAsync(Token);

            Assert.Equal(2, host.Servers.Count);
            Assert.Equal(4, host.Tools.Count);
            Assert.Equal(host.Tools.Count, host.Tools.Select(tool => tool.Definition.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.All(host.Tools, tool => Assert.Contains("_", tool.Definition.Name, StringComparison.Ordinal));
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static McpServerConnection Connect() =>
        new("test", CommandLine(), new McpClientOptions());

    private static string CommandLine() => $"\"{ServerPath()}\"";

    private static string ServerPath()
    {
        var name = OperatingSystem.IsWindows() ? "SharpCoreAgent.TestMcpServer.exe" : "SharpCoreAgent.TestMcpServer";
        var path = Path.Combine(AppContext.BaseDirectory, "McpServer", name);
        Assert.True(File.Exists(path), string.Create(CultureInfo.InvariantCulture, $"the test server was not copied to {path}"));
        return path;
    }

    private static JsonElement Arguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static int ServerPid(McpServerConnection connection)
    {
        var line = Assert.Single(connection.Stderr, entry => entry.StartsWith("pid=", StringComparison.Ordinal));
        return int.Parse(line["pid=".Length..], CultureInfo.InvariantCulture);
    }

    private static async Task<bool> WaitsForExitAsync(int pid)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                // No such process: the kill worked.
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), Token).ConfigureAwait(false);
        }

        return false;
    }
}
