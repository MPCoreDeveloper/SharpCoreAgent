using SharpCoreAgent.Infrastructure.Mcp;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The declarative file is an explicit, local-only list: two ecosystem keys, command/args/env, no URL entries.
/// </summary>
public sealed class McpServerFileTests
{
    [Fact]
    public void BothEcosystemKeysAreAccepted()
    {
        var claude = McpServerFile.Parse(
            """{"mcpServers":{"fs":{"command":"npx","args":["-y","server-filesystem","."],"env":{"ROOT":"/tmp"}}}}""");
        var vscode = McpServerFile.Parse(
            """{"servers":{"git":{"command":"uvx","args":["mcp-server-git"]}}}""");

        var server = Assert.Single(claude);
        Assert.Equal("fs", server.Name);
        Assert.Equal("npx -y server-filesystem .", server.CommandLine);
        Assert.Equal("/tmp", server.Environment["ROOT"]);
        Assert.Single(vscode);
    }

    [Fact]
    public void AnArgumentWithSpacesIsQuoted()
    {
        var server = Assert.Single(McpServerFile.Parse(
            """{"mcpServers":{"one":{"command":"tool","args":["two words"]}}}"""));

        Assert.Equal("tool \"two words\"", server.CommandLine);
    }

    [Fact]
    public void ARemoteEntryIsRefusedWithTheRemoteFlagsNamed()
    {
        var error = Assert.Throws<ArgumentException>(() => McpServerFile.Parse(
            """{"mcpServers":{"remote":{"url":"https://mcp.example/rpc"}}}"""));

        Assert.Contains("--mcp-url", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedEntryIsRefused()
    {
        Assert.Throws<ArgumentException>(() => McpServerFile.Parse(
            """{"mcpServers":{"missing":{"args":[]}}}"""));
        Assert.Throws<ArgumentException>(() => McpServerFile.Parse(
            """{"mcpServers":{"bad":{"command":"x","args":"not-an-array"}}}"""));
        Assert.Throws<ArgumentException>(() => McpServerFile.Parse(
            """{"mcpServers":{"bad":{"command":"x","env":{"A":1}}}}"""));
    }

    [Fact]
    public void AnEmptyListIsRefused()
    {
        Assert.Throws<ArgumentException>(() => McpServerFile.Parse("""{"mcpServers":{}}"""));
    }

    [Fact]
    public void ADocumentWithoutASupportedServerObjectIsRefused()
    {
        Assert.Throws<ArgumentException>(() => McpServerFile.Parse("""{"somethingElse":{}}"""));
    }
}
