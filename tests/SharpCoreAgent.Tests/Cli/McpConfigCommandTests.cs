using System.Text;
using System.Text.Json;
using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// <c>mcp config</c> end to end: four clients, three file shapes, one block on stdout and one line on stderr.
/// </summary>
/// <remarks>
/// The blocks are parsed back rather than compared with a golden string, because the point is that the agent will
/// accept them: the JSON through <see cref="JsonDocument"/>, the TOML by its three lines — the repository has no TOML
/// reader, and adding one to check three keys would be a dependency to maintain for a fixture. The merge
/// <c>--write</c> performs is <c>McpConfigWriteTests</c>'; these cases are about what is printed.
/// </remarks>
public sealed class McpConfigCommandTests
{
    [Fact]
    public async Task ClaudeGetsThePortableMcpServersBlock()
    {
        using var directory = TempDirectory.Create();

        var (exit, snippet, _) = await ConfigAsync(directory.Path, "claude");

        Assert.Equal(0, exit);

        using var document = JsonDocument.Parse(snippet);
        var entry = document.RootElement.GetProperty("mcpServers").GetProperty("sharpcoreagent");

        // No "type": the portable format infers stdio from `command`, and Claude Code does not need it.
        Assert.False(entry.TryGetProperty("type", out _));
        Assert.True(File.Exists(entry.GetProperty("command").GetString()));
        Assert.Equal("mcp|serve", Arguments(entry));
    }

    [Fact]
    public async Task VsCodeGetsTheServersBlockWithAnExplicitStdioType()
    {
        using var directory = TempDirectory.Create();

        var (exit, snippet, _) = await ConfigAsync(directory.Path, "vscode");

        Assert.Equal(0, exit);

        using var document = JsonDocument.Parse(snippet);
        var entry = document.RootElement.GetProperty("servers").GetProperty("sharpcoreagent");

        // VS Code's own format, not the portable one: `servers`, and a stdio entry says so.
        Assert.Equal("stdio", entry.GetProperty("type").GetString());
        Assert.True(File.Exists(entry.GetProperty("command").GetString()));
        Assert.False(document.RootElement.TryGetProperty("mcpServers", out _));
    }

    [Fact]
    public async Task VisualStudioGetsTheServersBlockInTheFileBesideTheSolution()
    {
        using var directory = TempDirectory.Create();

        var (exit, snippet, note) = await ConfigAsync(directory.Path, "visualstudio");

        Assert.Equal(0, exit);

        using var document = JsonDocument.Parse(snippet);
        var entry = document.RootElement.GetProperty("servers").GetProperty("sharpcoreagent");

        // The key Visual Studio reads, the file it reads it from, and the same explicit stdio type VS Code wants:
        // an entry that omits the type is the reason an IDE shows a server that never starts.
        Assert.Equal("stdio", entry.GetProperty("type").GetString());
        Assert.True(File.Exists(entry.GetProperty("command").GetString()));
        Assert.Equal("mcp|serve", Arguments(entry));
        Assert.False(document.RootElement.TryGetProperty("mcpServers", out _));

        Assert.Contains(Path.Combine(directory.Path, ".mcp.json"), note, StringComparison.Ordinal);
        Assert.Contains("solution scope", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodexGetsATomlTableWithALiteralPath()
    {
        using var directory = TempDirectory.Create();

        var (exit, snippet, _) = await ConfigAsync(directory.Path, "codex");

        Assert.Equal(0, exit);

        var lines = Lines(snippet);

        Assert.Equal("[mcp_servers.sharpcoreagent]", lines[0]);
        Assert.StartsWith("command = ", lines[1], StringComparison.Ordinal);

        // A literal TOML string, which is what keeps a Windows path readable: no doubled separators, no escapes.
        var command = lines[1]["command = ".Length..].Trim('\'');
        Assert.True(Path.IsPathRooted(command));
        Assert.True(File.Exists(command));
        Assert.DoesNotContain(@"\\", lines[1], StringComparison.Ordinal);
        Assert.Equal("args = ['mcp', 'serve']", lines[2]);
    }

    [Fact]
    public async Task TheNoteNamesTheFileEachClientReads()
    {
        using var directory = TempDirectory.Create();

        var (_, _, claude) = await ConfigAsync(directory.Path, "claude");
        var (_, _, visualstudio) = await ConfigAsync(directory.Path, "visualstudio");
        var (_, _, vscode) = await ConfigAsync(directory.Path, "vscode");
        var (_, _, codex) = await ConfigAsync(directory.Path, "codex");

        Assert.Contains(Path.Combine(directory.Path, ".mcp.json"), claude, StringComparison.Ordinal);
        Assert.Contains("project scope", claude, StringComparison.Ordinal);

        // The same file, named for the other client that reads it: the note is how a developer learns that one
        // committed .mcp.json can serve both, rather than that each client needs its own.
        Assert.Contains(Path.Combine(directory.Path, ".mcp.json"), visualstudio, StringComparison.Ordinal);
        Assert.Contains("solution scope", visualstudio, StringComparison.Ordinal);

        Assert.Contains(Path.Combine(directory.Path, ".vscode", "mcp.json"), vscode, StringComparison.Ordinal);
        Assert.Contains("workspace scope", vscode, StringComparison.Ordinal);

        Assert.Contains(".codex", codex, StringComparison.Ordinal);
        Assert.Contains("config.toml", codex, StringComparison.Ordinal);
        Assert.Contains("user scope", codex, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StdoutCarriesTheBlockAndNothingElse()
    {
        using var directory = TempDirectory.Create();

        var (_, snippet, note) = await ConfigAsync(directory.Path, "claude");

        // A snippet that carried "paste this into …" would be a file the agent cannot parse.
        Assert.DoesNotContain("paste", snippet, StringComparison.Ordinal);
        Assert.StartsWith("sharpcoreagent mcp config: paste into ", note, StringComparison.Ordinal);
        Assert.Single(Lines(note));
    }

    [Fact]
    public async Task TheBlockNamesThisBinarySoAPastedLineRuns()
    {
        using var directory = TempDirectory.Create();

        var (_, snippet, _) = await ConfigAsync(directory.Path, "vscode");
        using var document = JsonDocument.Parse(snippet);
        var entry = document.RootElement.GetProperty("servers").GetProperty("sharpcoreagent");
        var command = entry.GetProperty("command").GetString()!;

        // The property that matters: what the block names exists, and it ends in `mcp serve`. Under a test host the
        // executable is the .NET host, and then the assembly it should load has to be named first.
        Assert.True(Path.IsPathRooted(command));
        Assert.True(File.Exists(command));

        var arguments = Arguments(entry).Split('|');
        Assert.Equal("mcp|serve", string.Join('|', arguments[^2..]));

        if (string.Equals(Path.GetFileNameWithoutExtension(command), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            Assert.EndsWith(".dll", arguments[0], StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(arguments[0]));
        }
    }

    [Fact]
    public async Task TheCommandAnswersWithoutAWorkspaceOrAnIndex()
    {
        // Nothing is opened, so a path that does not exist is not a reason to refuse: the block is about
        // configuration, and a developer wiring up a client should not have to build an index first.
        var missing = Path.Combine(Path.GetTempPath(), "sharpcoreagent-mcp-config-missing", Guid.NewGuid().ToString("N"));

        var (exit, snippet, note) = await ConfigAsync(missing, "claude");

        Assert.Equal(0, exit);
        Assert.StartsWith("{", snippet, StringComparison.Ordinal);
        Assert.Contains(missing, note, StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Snippet, string Note)> ConfigAsync(string workspace, string client)
    {
        var options = CliOptions.Parse(["mcp", "config", "--client", client, "--workspace", workspace], _ => null);
        using var stdout = new MemoryStream();
        using var diagnostics = new StringWriter();

        var exit = await McpCommand.ExecuteAsync(
            options,
            Stream.Null,
            stdout,
            diagnostics,
            TestContext.Current.CancellationToken);

        return (exit, Encoding.UTF8.GetString(stdout.ToArray()), diagnostics.ToString());
    }

    private static string Arguments(JsonElement entry) =>
        string.Join('|', entry.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()));

    private static string[] Lines(string text) =>
        [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n')];
}

