using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The commands as a developer invokes them: the help text, the catalogue, one turn and the chat loop.
/// </summary>
/// <remarks>
/// The console streams are swapped for the duration of each test, so these exercise the commands themselves
/// rather than a rewired copy of them. That is also why the class is serialized: a process has one console.
/// </remarks>
[Collection(CliConsole.Name)]
public sealed class CliCommandTests
{
    [Fact]
    public void TheUsageTextDocumentsEveryOptionAndExitCode()
    {
        Assert.Contains("Usage", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("--provider", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("--no-session", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("--base-url", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("Exit codes", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("5  the transcript could not be read or written", HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChatHelpListsEverySlashCommand()
    {
        foreach (var command in new[] { "/model", "/approval", "/usage", "/files", "/session", "/help", "/exit", "/quit" })
        {
            Assert.Contains(command, HelpText.ChatCommands, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheVersionIsTheNumberWithoutTheSourceLinkHash()
    {
        Assert.NotEmpty(HelpText.Version);
        Assert.DoesNotContain("+", HelpText.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void TheHintPointsAtTheHelpCommand() =>
        Assert.Contains("--help", HelpText.Hint, StringComparison.Ordinal);

    [Fact]
    public void ModelsListsTheCatalogueOfOneProvider()
    {
        var cli = CliOptions.Parse(["models", "--provider", "demo"], NoEnvironment);
        using var console = new ConsoleScope(string.Empty);

        Assert.Equal(0, ModelsCommand.Execute(cli));

        var text = console.Out.ToString();
        Assert.Contains("demo:demo-scripted", text, StringComparison.Ordinal);
        Assert.DoesNotContain("deepseek", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelsListsEveryProviderAndNotesTheMissingCredentials()
    {
        var cli = CliOptions.Parse(["models"], NoEnvironment);
        using var console = new ConsoleScope(string.Empty);

        Assert.Equal(0, ModelsCommand.Execute(cli));

        var text = console.Out.ToString();
        Assert.Contains("deepseek:deepseek-chat", text, StringComparison.Ordinal);
        Assert.Contains("demo:demo-scripted", text, StringComparison.Ordinal);
        Assert.Contains("note: set", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCompletesATurnWithTheOfflineProvider()
    {
        using var directory = TempDirectory.Create();
        var cli = CliOptions.Parse(
            ["run", "list the files", "--provider", "demo", "--workspace", directory.Path, "--approval", "allow", "--no-session"],
            NoEnvironment);
        using var console = new ConsoleScope(string.Empty);

        var exitCode = await RunCommand.ExecuteAsync(cli, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("— completed", console.Error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("session:", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunWritesTheTranscriptUnlessItIsToldNotTo()
    {
        using var directory = TempDirectory.Create();
        var cli = CliOptions.Parse(
            ["run", "list the files", "--provider", "demo", "--workspace", directory.Path, "--approval", "allow"],
            NoEnvironment);
        using var console = new ConsoleScope(string.Empty);

        var exitCode = await RunCommand.ExecuteAsync(cli, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("session: ", console.Error.ToString(), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, ".sharpcoreagent", "sessions"), "*.json"));
    }

    [Fact]
    public async Task RunInAMissingWorkspaceIsAUsageError()
    {
        var cli = CliOptions.Parse(
            ["run", "hi", "--provider", "demo", "--workspace", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))],
            NoEnvironment);
        using var console = new ConsoleScope(string.Empty);

        Assert.Equal(2, await RunCommand.ExecuteAsync(cli, TestContext.Current.CancellationToken));
        Assert.Contains("does not exist", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatUnderstandsEverySlashCommand()
    {
        using var directory = TempDirectory.Create();
        var script = string.Join(
            '\n',
            "/help",
            "/model",
            "/model demo:demo-scripted",
            "/model deepseek:gpt-5",
            "/model nope:whatever",
            "/model demo:demo-scripted",
            "/approval readonly",
            "/approval nope",
            "/usage",
            "/files",
            "/session",
            "/nonsense",
            "list the files",
            "/usage",
            "/files",
            "/exit");
        var cli = CliOptions.Parse(
            ["chat", "--provider", "demo", "--workspace", directory.Path, "--approval", "allow"],
            NoEnvironment);
        using var console = new ConsoleScope(script);
        using var scope = new CancellationScope();

        Assert.Equal(0, await ChatCommand.ExecuteAsync(cli, scope));

        var output = console.Out.ToString();
        var error = console.Error.ToString();

        Assert.Contains("/exit, /quit", output, StringComparison.Ordinal);
        Assert.Contains("demo:demo-scripted", output, StringComparison.Ordinal);
        Assert.Contains("model: demo:demo-scripted", output, StringComparison.Ordinal);
        Assert.Contains("model: deepseek:gpt-5", output, StringComparison.Ordinal);
        Assert.Contains("unknown model 'nope:whatever'", error, StringComparison.Ordinal);
        Assert.Contains("approval: readonly", error, StringComparison.Ordinal);
        Assert.Contains("usage: /approval readonly|ask|allow", error, StringComparison.Ordinal);
        Assert.Contains("no usage reported yet", output, StringComparison.Ordinal);
        Assert.Contains("no turn has run yet", output, StringComparison.Ordinal);
        Assert.Contains("unknown command '/nonsense'", error, StringComparison.Ordinal);
        Assert.Contains("session: ", error, StringComparison.Ordinal);
        Assert.Contains("— completed", error, StringComparison.Ordinal);
        Assert.Contains("total  ", output, StringComparison.Ordinal);
        Assert.Contains("the last turn changed no files and ran no commands", output, StringComparison.Ordinal);
        Assert.Contains("workspace: ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatLeavesAtEndOfInput()
    {
        using var directory = TempDirectory.Create();
        var cli = CliOptions.Parse(
            ["chat", "--provider", "demo", "--workspace", directory.Path, "--no-session"],
            NoEnvironment);
        using var console = new ConsoleScope(string.Empty);
        using var scope = new CancellationScope();

        Assert.Equal(0, await ChatCommand.ExecuteAsync(cli, scope));
        Assert.Contains("workspace: ", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatBlanksAreIgnoredAndTheModelCommandReportsTheActiveOne()
    {
        using var directory = TempDirectory.Create();
        var cli = CliOptions.Parse(
            ["chat", "--provider", "demo", "--workspace", directory.Path, "--no-session", "--quiet"],
            NoEnvironment);
        using var console = new ConsoleScope($"\n\n   \n/model\n/session\n/quit\n");
        using var scope = new CancellationScope();

        Assert.Equal(0, await ChatCommand.ExecuteAsync(cli, scope));

        Assert.Contains("demo:demo-scripted", console.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("session saving is off (--no-session)", console.Out.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("workspace: ", console.Error.ToString(), StringComparison.Ordinal);
    }

    private static string? NoEnvironment(string name) => null;
}

/// <summary>Serializes the tests that swap the process console streams.</summary>
/// <remarks>
/// A process has one <see cref="Console"/>, so two tests that redirect it concurrently would read each
/// other's output. The collection is what keeps them apart without making the whole suite single-threaded.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliConsole
{
    private CliConsole()
    {
    }

    /// <summary>Gets the collection name.</summary>
    public const string Name = "cli-console";
}

/// <summary>A unique directory under the temp path, removed when the test ends.</summary>
internal sealed class TempDirectory : IDisposable
{
    private TempDirectory(string path) => Path = path;

    public string Path { get; }

    public static TempDirectory Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "sharpcoreagent-commands",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TempDirectory(path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a passing test over.
        }
    }
}

/// <summary>Swaps the process console for the duration of one command, and puts it back afterwards.</summary>
/// <remarks>
/// The encoding is deliberately not touched: the commands under test never change it, and setting
/// <see cref="Console.OutputEncoding"/> would discard the redirected writer these assertions read.
/// </remarks>
internal sealed class ConsoleScope : IDisposable
{
    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    private readonly TextReader _originalIn = Console.In;
    private readonly string? _originalSettings = Environment.GetEnvironmentVariable("SHARPCOREAGENT_CONFIG");
    private readonly TempDirectory? _settingsDirectory;

    public ConsoleScope(string input, string? settingsPath = null)
    {
        // Every command now reads the settings file, so a test that did not pin one would read the
        // developer's own and pass or fail depending on what it says. A directory per scope keeps the
        // suite hermetic without asking each test to remember.
        _settingsDirectory = settingsPath is null ? TempDirectory.Create() : null;
        SettingsPath = settingsPath ?? System.IO.Path.Combine(_settingsDirectory!.Path, "config.json");
        Environment.SetEnvironmentVariable("SHARPCOREAGENT_CONFIG", SettingsPath);

        Console.SetOut(Out);
        Console.SetError(Error);
        Console.SetIn(new StringReader(input));
    }

    public StringWriter Out { get; } = new();

    public StringWriter Error { get; } = new();

    /// <summary>Gets the settings file this scope pinned, for a test that wants to write one.</summary>
    public string SettingsPath { get; }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        Console.SetIn(_originalIn);
        Environment.SetEnvironmentVariable("SHARPCOREAGENT_CONFIG", _originalSettings);
        _settingsDirectory?.Dispose();
    }
}
