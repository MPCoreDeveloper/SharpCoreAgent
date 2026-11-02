using System.Text;
using System.Text.Json;
using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// <c>mcp config --write</c>: the merge, the refusal, and the one line that says which of the three happened.
/// </summary>
/// <remarks>
/// Every case parses the file it just wrote, because the claim is about a file a client will read rather than about
/// the characters this command produced. All three outcomes exit 0 — a file that already says the right thing is a
/// success, not a no-op to be punished — and the only failure is a file that cannot be merged, which exits 7 with the
/// file exactly as it was.
/// </remarks>
public sealed class McpConfigWriteTests
{
    [Fact]
    public async Task WriteCreatesTheFileAndSaysSo()
    {
        using var directory = TempDirectory.Create();

        var (exit, stdout, note) = await WriteAsync(directory.Path, "visualstudio");

        Assert.Equal(0, exit);

        // Nothing for a redirect: the block went into the file, and a second copy on stdout would be a copy a caller
        // piping this command would append to the one it just merged.
        Assert.Empty(stdout);

        using var document = Read(Path.Combine(directory.Path, ".mcp.json"));
        var entry = document.RootElement.GetProperty("servers").GetProperty("sharpcoreagent");

        Assert.Equal("stdio", entry.GetProperty("type").GetString());
        Assert.True(File.Exists(entry.GetProperty("command").GetString()));
        Assert.EndsWith("mcp|serve", Arguments(entry), StringComparison.Ordinal);

        Assert.StartsWith("sharpcoreagent mcp config: wrote ", note, StringComparison.Ordinal);
        Assert.Contains("created", note, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(directory.Path, ".mcp.json"), note, StringComparison.Ordinal);
        Assert.Single(Lines(note));
    }

    [Fact]
    public async Task WriteKeepsEveryOtherKeyAndServer()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, ".mcp.json");
        await File.WriteAllTextAsync(path, """
            {
              "$schema": "https://example.invalid/mcp.json",
              "mcpServers": { "other": { "command": "other.exe" } }
            }
            """, TestContext.Current.CancellationToken);

        var (exit, _, note) = await WriteAsync(directory.Path, "visualstudio");

        Assert.Equal(0, exit);
        Assert.Contains("created", note, StringComparison.Ordinal);

        using var document = Read(path);

        // The file's own keys and the other client's servers are the parts this command has no opinion about.
        Assert.Equal(
            "https://example.invalid/mcp.json",
            document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal(
            "other.exe",
            document.RootElement.GetProperty("mcpServers").GetProperty("other").GetProperty("command").GetString());
        Assert.True(document.RootElement.GetProperty("servers").TryGetProperty("sharpcoreagent", out _));
    }

    [Fact]
    public async Task OneCommittedFileCarriesBothKeys()
    {
        // The case the whole flag exists for: one .mcp.json beside the solution, read by Claude Code under
        // mcpServers and by Visual Studio under servers, with neither run destroying the other's key.
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, ".mcp.json");

        var (claudeExit, _, _) = await WriteAsync(directory.Path, "claude");
        var (studioExit, _, note) = await WriteAsync(directory.Path, "visualstudio");

        Assert.Equal(0, claudeExit);
        Assert.Equal(0, studioExit);
        Assert.Contains("created", note, StringComparison.Ordinal);

        using var document = Read(path);
        var claude = document.RootElement.GetProperty("mcpServers").GetProperty("sharpcoreagent");
        var studio = document.RootElement.GetProperty("servers").GetProperty("sharpcoreagent");

        Assert.Equal(claude.GetProperty("command").GetString(), studio.GetProperty("command").GetString());
        Assert.Equal("stdio", studio.GetProperty("type").GetString());
        Assert.False(claude.TryGetProperty("type", out _));
    }

    [Fact]
    public async Task WritingTwiceLeavesTheFileAlone()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, ".mcp.json");

        await WriteAsync(directory.Path, "visualstudio");
        var first = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        var (exit, stdout, note) = await WriteAsync(directory.Path, "visualstudio");

        Assert.Equal(0, exit);
        Assert.Empty(stdout);
        Assert.Contains("nothing written", note, StringComparison.Ordinal);
        Assert.Equal(first, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteKeepsAnEnvBlockAndReplacesAStaleCommand()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, ".mcp.json");
        await File.WriteAllTextAsync(path, """
            {
              "servers": {
                "sharpcoreagent": {
                  "type": "stdio",
                  "command": "C:\\tools\\old\\sharpcoreagent.exe",
                  "args": [ "mcp", "serve" ],
                  "env": { "DOTNET_ENVIRONMENT": "Development" }
                }
              }
            }
            """, TestContext.Current.CancellationToken);

        var (exit, _, note) = await WriteAsync(directory.Path, "visualstudio");

        Assert.Equal(0, exit);
        Assert.Contains("updated", note, StringComparison.Ordinal);

        using var document = Read(path);
        var entry = document.RootElement.GetProperty("servers").GetProperty("sharpcoreagent");

        // What describes this binary is now true; what the developer added to the entry is still there.
        Assert.NotEqual(@"C:\tools\old\sharpcoreagent.exe", entry.GetProperty("command").GetString());
        Assert.Equal("Development", entry.GetProperty("env").GetProperty("DOTNET_ENVIRONMENT").GetString());
        Assert.EndsWith("mcp|serve", Arguments(entry), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteCreatesTheVsCodeFolder()
    {
        // A repository that has never had a .vscode directory is the common case, and the command's job is that the
        // next step is not "make the folder first".
        using var directory = TempDirectory.Create();

        var (exit, _, note) = await WriteAsync(directory.Path, "vscode");

        var path = Path.Combine(directory.Path, ".vscode", "mcp.json");
        Assert.Equal(0, exit);
        Assert.True(File.Exists(path));
        Assert.Contains(path, note, StringComparison.Ordinal);

        using var document = Read(path);
        Assert.True(document.RootElement.GetProperty("servers").TryGetProperty("sharpcoreagent", out _));
        Assert.False(document.RootElement.TryGetProperty("mcpServers", out _));
    }

    [Theory]
    [InlineData("visualstudio")]
    [InlineData("vscode")]
    public async Task AFileThatIsNotJsonIsRefusedAndLeftAsItWas(string client)
    {
        // Exit 7, not 2: the command line was understood, and the thing to fix is the file. Nothing is written, so
        // the file the developer opens afterwards is the file this command read.
        using var directory = TempDirectory.Create();
        var path = McpClientSnippets.Destination(
            client == "vscode" ? McpClientKind.VsCode : McpClientKind.VisualStudio,
            directory.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string broken = "{ \"servers\": ";
        await File.WriteAllTextAsync(path, broken, TestContext.Current.CancellationToken);

        var (exit, stdout, note) = await WriteAsync(directory.Path, client);

        Assert.Equal(7, exit);
        Assert.Empty(stdout);
        Assert.Contains("not valid JSON", note, StringComparison.Ordinal);
        Assert.Contains(path, note, StringComparison.Ordinal);
        Assert.Equal(broken, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AServersKeyThatIsNotAnObjectIsRefused()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, ".mcp.json");
        const string seeded = """{ "servers": [] }""";
        await File.WriteAllTextAsync(path, seeded, TestContext.Current.CancellationToken);

        var (exit, _, note) = await WriteAsync(directory.Path, "visualstudio");

        Assert.Equal(7, exit);
        Assert.Contains("holds a JSON Array, not an object", note, StringComparison.Ordinal);
        Assert.Equal(seeded, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    private static async Task<(int Exit, string Output, string Diagnostics)> WriteAsync(string workspace, string client)
    {
        var options = CliOptions.Parse(
            ["mcp", "config", "--client", client, "--write", "--workspace", workspace],
            _ => null);

        using var stdout = new MemoryStream();
        using var diagnostics = new StringWriter();

        var exit = await McpCommand.ExecuteAsync(
            options,
            Stream.Null,
            stdout,
            diagnostics,
            TestContext.Current.CancellationToken);

        return (exit, Encoding.UTF8.GetString(stdout.ToArray()), diagnostics.ToString().TrimEnd('\r', '\n'));
    }

    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));

    private static string Arguments(JsonElement entry) =>
        string.Join('|', entry.GetProperty("args").EnumerateArray().Select(argument => argument.GetString()));

    private static string[] Lines(string text) =>
        [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n')];
}

