using System.Text;
using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// <c>ide agent --write</c>: the profile written, replaced, left alone, and refused.
/// </summary>
/// <remarks>
/// The claim is about a file an editor will read, so every case reads the file back rather than inspecting what the
/// command produced. Three outcomes exit 0 — a file that already says this is a success — and the only failure is a
/// profile this command did not write, which exits 7 with the bytes it found.
/// </remarks>
public sealed class IdeAgentWriteTests
{
    [Fact]
    public async Task WriteCreatesTheProfileAndSaysSo()
    {
        using var directory = TempDirectory.Create();

        var (exit, stdout, note) = await RunAsync(directory.Path, ["--write"]);

        Assert.Equal(0, exit);

        // Nothing on stdout: the file was written, and a copy printed beside it would be a copy a caller pipes into the
        // file it just wrote.
        Assert.Empty(stdout);

        var path = AgentProfile.Destination(directory.Path);
        Assert.True(File.Exists(path));
        Assert.Equal(AgentProfile.Render(), await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        Assert.StartsWith("sharpcoreagent ide agent: wrote ", note, StringComparison.Ordinal);
        Assert.Contains("created", note, StringComparison.Ordinal);
        Assert.Contains(path, note, StringComparison.Ordinal);
        Assert.Single(Lines(note));
    }

    [Fact]
    public async Task ASecongRunLeavesTheIdenticalFileAlone()
    {
        using var directory = TempDirectory.Create();
        var path = AgentProfile.Destination(directory.Path);

        await RunAsync(directory.Path, ["--write"]);
        var first = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        var (exit, stdout, note) = await RunAsync(directory.Path, ["--write"]);

        Assert.Equal(0, exit);
        Assert.Empty(stdout);
        Assert.Contains("already says this; nothing written", note, StringComparison.Ordinal);
        Assert.Equal(first, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AProfileThisCommandWroteIsReplacedWhole()
    {
        using var directory = TempDirectory.Create();
        var path = AgentProfile.Destination(directory.Path);

        // An older profile: ours by the marker, and out of date everywhere else.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            $"---\ndescription: an older agent\n---\n\nolder prompt\n{AgentProfile.Marker}\n",
            TestContext.Current.CancellationToken);

        var (exit, _, note) = await RunAsync(directory.Path, ["--write"]);

        Assert.Equal(0, exit);
        Assert.Contains("updated", note, StringComparison.Ordinal);
        Assert.Equal(AgentProfile.Render(), await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AProfileSomebodyElseWroteIsRefusedAndLeftAsItWas()
    {
        using var directory = TempDirectory.Create();
        var path = AgentProfile.Destination(directory.Path);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string mine = "---\ndescription: a profile a developer wrote\n---\n\nand a prompt they wrote.\n";
        await File.WriteAllTextAsync(path, mine, TestContext.Current.CancellationToken);

        var (exit, stdout, note) = await RunAsync(directory.Path, ["--write"]);

        // Exit 7, not 2: the command line was understood, and the thing that needs a human is a file.
        Assert.Equal(7, exit);
        Assert.Empty(stdout);
        Assert.Contains("was not written by this command", note, StringComparison.Ordinal);
        Assert.Contains("move it aside", note, StringComparison.Ordinal);
        Assert.Equal(mine, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PrintingWritesTheProfileToStdoutAndNamesTheFile()
    {
        using var directory = TempDirectory.Create();

        var (exit, stdout, note) = await RunAsync(directory.Path, []);

        Assert.Equal(0, exit);
        Assert.Equal(AgentProfile.Render(), stdout);
        Assert.False(File.Exists(AgentProfile.Destination(directory.Path)));

        Assert.Contains(AgentProfile.Destination(directory.Path), note, StringComparison.Ordinal);
        Assert.Contains("--write writes it there", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheAgentsDirectoryIsCreatedWhenTheWorkspaceHasNone()
    {
        using var directory = TempDirectory.Create();

        Assert.False(Directory.Exists(Path.Combine(directory.Path, ".github")));

        await RunAsync(directory.Path, ["--write"]);

        Assert.True(Directory.Exists(Path.Combine(directory.Path, ".github", "agents")));
    }

    private static async Task<(int Exit, string Output, string Diagnostics)> RunAsync(
        string workspace,
        string[] flags)
    {
        var options = CliOptions.Parse(
            ["ide", "agent", "--workspace", workspace, .. flags],
            _ => null);

        using var stdout = new MemoryStream();
        using var diagnostics = new StringWriter();

        var exit = await IdeCommand.ExecuteAsync(
            options,
            stdout,
            diagnostics,
            TestContext.Current.CancellationToken);

        return (exit, Encoding.UTF8.GetString(stdout.ToArray()), diagnostics.ToString().TrimEnd('\r', '\n'));
    }

    private static string[] Lines(string text) =>
        [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n')];
}
