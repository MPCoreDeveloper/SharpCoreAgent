using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The promises only a real child process can keep: the launch path works on this platform, a server's stderr
/// never reaches stdout, disposing the connection kills the process tree, and a server that dies is restarted on
/// the next call without replaying the call that failed.
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

    [Fact]
    public async Task AServerKilledBetweenCallsIsRestartedForTheNextCall()
    {
        await using var connection = Connect();
        await connection.StartAsync(Token);
        var firstPid = LatestServerPid(connection);

        KillServer(firstPid);
        Assert.True(await WaitsForExitAsync(firstPid));
        await WaitUntilNotRunningAsync(connection);

        var echoed = await connection.CallAsync(
            "echo",
            Arguments("""{"text":"after the kill"}"""),
            TimeSpan.FromSeconds(30),
            Token);

        Assert.False(echoed.IsError);
        Assert.Equal("after the kill", echoed.Text);
        Assert.Equal(1, connection.RestartCount);
        Assert.Equal(2, connection.Stderr.Count(line => line.StartsWith("pid=", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ACrashedCallIsNotReplayedAndTheNextCallRestartsTheServer()
    {
        await using var connection = Connect(crash: true);
        await connection.StartAsync(Token);

        Assert.Contains("crash", connection.Tools.Select(tool => tool.Name));

        var crashed = await connection.CallAsync(
            "crash",
            NoArguments.RootElement.Clone(),
            TimeSpan.FromSeconds(30),
            Token);

        Assert.True(crashed.IsError);
        Assert.Contains("stopped answering", crashed.Text, StringComparison.Ordinal);
        Assert.Contains("The next call will restart it", crashed.Text, StringComparison.Ordinal);

        var echoed = await connection.CallAsync(
            "echo",
            Arguments("""{"text":"after the crash"}"""),
            TimeSpan.FromSeconds(30),
            Token);

        Assert.False(echoed.IsError);
        Assert.Equal("after the crash", echoed.Text);
        Assert.Equal(1, connection.RestartCount);

        // If the failed call had been replayed, the replacement server would have recorded a second crash and
        // died before answering the echo. Exactly one line is the proof that the call stayed failed.
        Assert.Single(connection.Stderr, line => line == "crash requested");
    }

    [Fact]
    public async Task RestartsStopAtTheConfiguredCeiling()
    {
        await using var connection = Connect(new McpClientOptions { MaxRestarts = 1 });
        await connection.StartAsync(Token);

        KillServer(LatestServerPid(connection));
        await WaitUntilNotRunningAsync(connection);
        var once = await connection.CallAsync(
            "echo",
            Arguments("""{"text":"first restart"}"""),
            TimeSpan.FromSeconds(30),
            Token);
        Assert.False(once.IsError);

        KillServer(LatestServerPid(connection));
        await WaitUntilNotRunningAsync(connection);
        var exhausted = await connection.CallAsync(
            "echo",
            Arguments("""{"text":"no restart left"}"""),
            TimeSpan.FromSeconds(30),
            Token);

        Assert.True(exhausted.IsError);
        Assert.Contains("could not be restarted", exhausted.Text, StringComparison.Ordinal);
        Assert.Contains("MaxRestarts 1", exhausted.Text, StringComparison.Ordinal);
        Assert.Equal(1, connection.RestartCount);
    }

    [Fact]
    public async Task SupervisionCanBeTurnedOff()
    {
        await using var connection = Connect(new McpClientOptions { MaxRestarts = 0 });
        await connection.StartAsync(Token);

        KillServer(LatestServerPid(connection));
        await WaitUntilNotRunningAsync(connection);

        var failed = await connection.CallAsync(
            "echo",
            Arguments("""{"text":"no supervision"}"""),
            TimeSpan.FromSeconds(30),
            Token);

        Assert.True(failed.IsError);
        Assert.Contains("supervision is off", failed.Text, StringComparison.Ordinal);
        Assert.Equal(0, connection.RestartCount);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static McpServerConnection Connect(McpClientOptions? options = null, bool crash = false) =>
        new("test", crash ? CrashCommandLine() : CommandLine(), options ?? new McpClientOptions());

    private static string CommandLine() => $"\"{ServerPath()}\"";

    private static string CrashCommandLine() => $"\"{ServerPath()}\" --crash-tool";

    private static void KillServer(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            process.WaitForExit(5_000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // The server was already gone; the assertions below still describe what happened.
        }
    }

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

    private static int LatestServerPid(McpServerConnection connection)
    {
        var line = connection.Stderr.Last(entry => entry.StartsWith("pid=", StringComparison.Ordinal));
        return int.Parse(line["pid=".Length..], CultureInfo.InvariantCulture);
    }

    private static async Task WaitUntilNotRunningAsync(McpServerConnection connection)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (!connection.IsRunning)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), Token).ConfigureAwait(false);
        }

        Assert.False(connection.IsRunning, "the server's pipe never noticed that its process was gone");
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
