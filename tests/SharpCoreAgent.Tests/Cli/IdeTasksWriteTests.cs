using System.Text;
using System.Text.Json;
using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// <c>ide tasks --write</c>: one task filed into a document somebody else may already own.
/// </summary>
/// <remarks>
/// Every case parses the file it just wrote, because the claim is about a document the editor reads. The merge is by
/// label: a second run must replace the task rather than add a twin, and everything this command does not own — another
/// task, another top-level key, the file's own bytes when nothing changed — survives.
/// </remarks>
public sealed class IdeTasksWriteTests
{
    [Fact]
    public async Task WriteCreatesTheDocumentAndSaysSo()
    {
        using var directory = TempDirectory.Create();

        var (exit, stdout, note) = await RunAsync(directory.Path, ["--write"]);

        Assert.Equal(0, exit);
        Assert.Empty(stdout);

        var path = WorkspaceTasks.Destination(directory.Path);
        Assert.True(File.Exists(path));

        using var document = Read(path);
        Assert.Equal("2.0.0", document.RootElement.GetProperty("version").GetString());

        var tasks = document.RootElement.GetProperty("tasks").EnumerateArray().ToArray();
        var task = Assert.Single(tasks);
        Assert.Equal("sharpcoreagent: index", task.GetProperty("label").GetString());
        Assert.True(File.Exists(task.GetProperty("command").GetString()));

        // The last argument is the verb: whatever precedes it is the .NET host's assembly, when this runs under the host.
        Assert.Equal("index", task.GetProperty("args").EnumerateArray().Last().GetString());

        Assert.StartsWith("sharpcoreagent ide tasks: wrote ", note, StringComparison.Ordinal);
        Assert.Contains("created", note, StringComparison.Ordinal);
        Assert.Contains(path, note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteKeepsEveryOtherTaskAndKey()
    {
        using var directory = TempDirectory.Create();
        var path = WorkspaceTasks.Destination(directory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """
            {
              "version": "2.0.0",
              "windows": { "options": { "shell": { "executable": "cmd.exe" } } },
              "tasks": [ { "label": "build", "command": "dotnet", "args": [ "build" ] } ]
            }
            """,
            TestContext.Current.CancellationToken);

        var (exit, _, note) = await RunAsync(directory.Path, ["--write"]);

        Assert.Equal(0, exit);
        Assert.Contains("updated", note, StringComparison.Ordinal);

        using var document = Read(path);
        Assert.True(document.RootElement.TryGetProperty("windows", out var windows));
        Assert.Equal(
            "cmd.exe",
            windows.GetProperty("options").GetProperty("shell").GetProperty("executable").GetString());

        var labels = document.RootElement.GetProperty("tasks").EnumerateArray()
            .Select(task => task.GetProperty("label").GetString())
            .ToArray();

        Assert.Equal(["build", "sharpcoreagent: index"], labels);
    }

    [Fact]
    public async Task ASecondRunReplacesTheTaskRatherThanAddingATwin()
    {
        using var directory = TempDirectory.Create();
        var path = WorkspaceTasks.Destination(directory.Path);

        await RunAsync(directory.Path, ["--write"]);
        var (exit, _, note) = await RunAsync(directory.Path, ["--write"]);

        Assert.Equal(0, exit);
        Assert.Contains("already carries the task", note, StringComparison.Ordinal);

        using var document = Read(path);
        Assert.Single(document.RootElement.GetProperty("tasks").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task AStaleTaskWithOurLabelIsReplacedInPlace()
    {
        using var directory = TempDirectory.Create();
        var path = WorkspaceTasks.Destination(directory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            """
            {
              "version": "2.0.0",
              "tasks": [
                { "label": "first", "command": "one" },
                { "label": "sharpcoreagent: index", "command": "somewhere/else" },
                { "label": "last", "command": "three" }
              ]
            }
            """,
            TestContext.Current.CancellationToken);

        var (exit, _, note) = await RunAsync(directory.Path, ["--write"]);

        Assert.Equal(0, exit);
        Assert.Contains("replaced the task 'sharpcoreagent: index'", note, StringComparison.Ordinal);

        using var document = Read(path);
        var tasks = document.RootElement.GetProperty("tasks").EnumerateArray().ToArray();

        // Same position, new command: a developer who found the task third still finds it third.
        Assert.Equal(["first", "sharpcoreagent: index", "last"], tasks.Select(t => t.GetProperty("label").GetString()));
        Assert.NotEqual("somewhere/else", tasks[1].GetProperty("command").GetString());
        Assert.True(File.Exists(tasks[1].GetProperty("command").GetString()));
    }

    [Theory]
    [InlineData("{ \"tasks\": ")]
    [InlineData("// a comment VS Code accepts and this build does not\n{ \"version\": \"2.0.0\", \"tasks\": [] }")]
    public async Task AFileThatIsNotJsonIsRefusedAndLeftAsItWas(string seeded)
    {
        using var directory = TempDirectory.Create();
        var path = WorkspaceTasks.Destination(directory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, seeded, TestContext.Current.CancellationToken);

        var (exit, stdout, note) = await RunAsync(directory.Path, ["--write"]);

        Assert.Equal(7, exit);
        Assert.Empty(stdout);
        Assert.Contains("is not valid JSON", note, StringComparison.Ordinal);
        Assert.Contains(path, note, StringComparison.Ordinal);
        Assert.Equal(seeded, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATasksKeyThatIsNotAnArrayIsRefused()
    {
        using var directory = TempDirectory.Create();
        var path = WorkspaceTasks.Destination(directory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string seeded = """{ "version": "2.0.0", "tasks": { "label": "one" } }""";
        await File.WriteAllTextAsync(path, seeded, TestContext.Current.CancellationToken);

        var (exit, _, note) = await RunAsync(directory.Path, ["--write"]);

        Assert.Equal(7, exit);
        Assert.Contains("not an array", note, StringComparison.Ordinal);
        Assert.Equal(seeded, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PrintingWritesTheDocumentToStdoutAndNamesTheFile()
    {
        using var directory = TempDirectory.Create();

        var (exit, stdout, note) = await RunAsync(directory.Path, []);

        Assert.Equal(0, exit);
        Assert.Equal(WorkspaceTasks.Render(), stdout);
        Assert.False(File.Exists(WorkspaceTasks.Destination(directory.Path)));

        Assert.Contains(WorkspaceTasks.Destination(directory.Path), note, StringComparison.Ordinal);
        Assert.Contains("--write merges this task into it", note, StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Output, string Diagnostics)> RunAsync(
        string workspace,
        string[] flags)
    {
        var options = CliOptions.Parse(
            ["ide", "tasks", "--workspace", workspace, .. flags],
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

    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllText(path));
}
