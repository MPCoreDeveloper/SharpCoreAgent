using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The launch rule is one line of decision with a platform branch, and getting it wrong turns every command line a
/// developer types into a path error. These tests pin both branches.
/// </summary>
public sealed class McpLauncherTests
{
    [Fact]
    public void TheCommandRunsThroughThePlatformShell()
    {
        var (fileName, arguments, rawArguments) = McpLauncher.For("npx -y @modelcontextprotocol/server-filesystem .");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd.exe", fileName);
            Assert.Equal(["/d", "/s", "/c"], arguments);
            Assert.Equal("/d /s /c \"npx -y @modelcontextprotocol/server-filesystem .\"", rawArguments);
        }
        else
        {
            Assert.Equal("/bin/sh", fileName);
            Assert.Equal(["-c", "npx -y @modelcontextprotocol/server-filesystem ."], arguments);
            Assert.Null(rawArguments);
        }
    }

    [Fact]
    public void AQuotedCommandKeepsItsOwnQuotes()
    {
        var (_, _, rawArguments) = McpLauncher.For("  \"C:\\Program Files\\srv.exe\" --flag  ");

        if (OperatingSystem.IsWindows())
        {
            // The developer's quotes are passed through as typed; escaping them would make cmd try to run a path
            // that starts with a backslash-quote.
            Assert.Equal("/d /s /c \"\"C:\\Program Files\\srv.exe\" --flag\"", rawArguments);
        }
    }

    [Theory]
    [InlineData("npx -y pkg", "npx")]
    [InlineData("uvx mcp-server-git", "uvx")]
    [InlineData("\"C:\\tools\\mcp-server.cmd\" --flag", "mcp-server")]
    [InlineData("  ", "")]
    public void TheStemNamesTheServer(string commandLine, string expected) =>
        Assert.Equal(expected, McpLauncher.Stem(commandLine));

    [Fact]
    public void ABlankCommandIsRefused() =>
        Assert.Throws<ArgumentException>(() => McpLauncher.For("   "));
}
