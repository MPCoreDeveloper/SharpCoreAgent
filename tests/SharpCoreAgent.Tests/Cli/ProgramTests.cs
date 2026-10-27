using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The process entry point: every documented exit code, reached the way a developer reaches it.
/// </summary>
/// <remarks>
/// <see cref="Program.Main"/> is invoked through reflection because it is the process's own entry point and
/// deliberately not public: a test that called a friendlier overload would not be testing the mapping that
/// scripts depend on. The console is swapped, so the assertions read the product's output.
/// </remarks>
[Collection(CliConsole.Name)]
public sealed class ProgramTests
{
    [Fact]
    public async Task NoArgumentsPrintsTheUsageBlock()
    {
        // The probe is stated rather than read from the console: whether this test host owns a terminal
        // depends on how the suite was launched, and the assertion must not.
        var previous = Terminal.Interactive;
        Terminal.Interactive = () => false;

        try
        {
            var result = await RunAsync([]);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Usage", result.Out, StringComparison.Ordinal);
            Assert.Contains("--approval", result.Out, StringComparison.Ordinal);
        }
        finally
        {
            Terminal.Interactive = previous;
        }
    }

    [Fact]
    public async Task ConfigSetThenGetRoundTripsThroughTheProgram()
    {
        using var directory = TempDirectory.Create();
        var settings = Path.Combine(directory.Path, "config.json");

        var set = await RunAsync(["config", "set", "approval", "readonly"], settingsPath: settings);
        Assert.Equal(0, set.ExitCode);
        Assert.Contains("set approval", set.Out, StringComparison.Ordinal);

        var get = await RunAsync(["config", "get", "approval"], settingsPath: settings);
        Assert.Equal(0, get.ExitCode);
        Assert.Equal("readonly" + Environment.NewLine, get.Out);
    }

    [Fact]
    public async Task ConfigShowReportsTheValueAndItsSource()
    {
        using var directory = TempDirectory.Create();
        var settings = Path.Combine(directory.Path, "config.json");
        await File.WriteAllTextAsync(
            settings,
            """{"approval":"readonly"}""",
            TestContext.Current.CancellationToken);

        var result = await RunAsync(["config"], settingsPath: settings);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("readonly", result.Out, StringComparison.Ordinal);
        Assert.Contains("file", result.Out, StringComparison.Ordinal);
        Assert.Contains(settings, result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrokenSettingsFileExitsSevenAndNamesTheFile()
    {
        using var directory = TempDirectory.Create();
        var settings = Path.Combine(directory.Path, "config.json");
        await File.WriteAllTextAsync(settings, "{ not json", TestContext.Current.CancellationToken);

        var result = await RunAsync(["models", "--provider", "demo"], settingsPath: settings);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains(settings, result.Error, StringComparison.Ordinal);
        Assert.Contains("hint: ", result.Error, StringComparison.Ordinal);

        // The verb that repairs the file must keep working while it is broken.
        var path = await RunAsync(["config", "path"], settingsPath: settings);
        Assert.Equal(0, path.ExitCode);
        Assert.Equal(settings + Environment.NewLine, path.Out);
    }

    [Fact]
    public async Task ASettingsValueTheFlagWouldRejectExitsSevenWithItsKey()
    {
        using var directory = TempDirectory.Create();
        var settings = Path.Combine(directory.Path, "config.json");
        await File.WriteAllTextAsync(
            settings,
            """{"approval":"sometimes"}""",
            TestContext.Current.CancellationToken);

        var result = await RunAsync(["config", "get", "approval"], settingsPath: settings);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("approval", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VersionPrintsTheProductNameAndNumber()
    {
        var result = await RunAsync(["--version"]);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("Ariadne ", result.Out, StringComparison.Ordinal);
        Assert.Contains("(SharpCoreAgent)", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpIsTheSameAsNoArguments()
    {
        var result = await RunAsync(["--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("sharpcoreagent run", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMistypedOptionIsAUsageErrorWithTheHint()
    {
        var result = await RunAsync(["run", "hello", "--nope"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("unknown option '--nope'", result.Error, StringComparison.Ordinal);
        Assert.Contains("hint: ", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingPromptIsAUsageErrorWithItsOwnHint()
    {
        var result = await RunAsync(["run"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("run needs a prompt.", result.Error, StringComparison.Ordinal);
        Assert.Contains("For example", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelsIsAnsweredWithoutATurn()
    {
        var result = await RunAsync(["models", "--provider", "demo"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("demo-scripted", result.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunAgainstTheOfflineProviderExitsZero()
    {
        using var directory = TempDirectory.Create();
        var result = await RunAsync(
            ["run", "list the files", "--provider", "demo", "--workspace", directory.Path, "--approval", "allow", "--no-session"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("— completed", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJsonRunEmitsMachineReadableLines()
    {
        using var directory = TempDirectory.Create();
        var result = await RunAsync(
            ["run", "list the files", "--provider", "demo", "--workspace", directory.Path, "--approval", "allow", "--json", "--no-session"]);

        Assert.Equal(0, result.ExitCode);
        var lines = result.Out.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.StartsWith("{", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMissingWorkspaceExitsWithTheUsageCode()
    {
        var result = await RunAsync(
            ["run", "hi", "--provider", "demo", "--workspace", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("does not exist", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingCredentialExitsWithTheCredentialCode()
    {
        var original = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", null);

        try
        {
            using var directory = TempDirectory.Create();
            var result = await RunAsync(
                ["run", "hi", "--provider", "deepseek", "--workspace", directory.Path, "--no-session"]);

            Assert.Equal(3, result.ExitCode);
            Assert.Contains("DEEPSEEK_API_KEY", result.Error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", original);
        }
    }

    [Fact]
    public async Task ChatEndsAtEndOfInputWithTheSuccessCode()
    {
        using var directory = TempDirectory.Create();
        var result = await RunAsync(
            ["chat", "--provider", "demo", "--workspace", directory.Path, "--no-session"],
            input: string.Empty);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task AMissingCredentialFileExitsWithTheCredentialCode()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "keys.env");

        var result = await RunAsync(["models", "--credential-file", path]);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains(path, result.Error, StringComparison.Ordinal);
        Assert.Contains("could not be read", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMalformedCredentialFileNamesTheLineAndNotItsContent()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        await File.WriteAllTextAsync(
            path,
            "DEEPSEEK_API_KEY sk-not-a-real-key" + Environment.NewLine,
            TestContext.Current.CancellationToken);

        var result = await RunAsync(
            ["run", "hi", "--provider", "deepseek", "--workspace", directory.Path, "--credential-file", path, "--no-session"]);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("line 1", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-not-a-real-key", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelsFindsTheKeyInTheFileAndPrintsNoNote()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "keys.env");
        await File.WriteAllLinesAsync(
            path,
            ["# rotated today", "DEEPSEEK_API_KEY=sk-not-a-real-key"],
            TestContext.Current.CancellationToken);

        var result = await RunAsync(["models", "--provider", "deepseek", "--credential-file", path]);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("note:", result.Out, StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Out, string Error)> RunAsync(
        string[] args,
        string? input = null,
        string? settingsPath = null)
    {
        using var console = new ConsoleScope(input ?? string.Empty, settingsPath);

        var exitCode = await ProgramEntry.RunAsync(args).ConfigureAwait(false);

        return (exitCode, console.Out.ToString(), console.Error.ToString());
    }
}
