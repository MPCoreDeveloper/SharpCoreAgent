using System.Reflection;
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
        var result = await RunAsync([]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage", result.Out, StringComparison.Ordinal);
        Assert.Contains("--approval", result.Out, StringComparison.Ordinal);
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

    private static async Task<(int ExitCode, string Out, string Error)> RunAsync(string[] args, string? input = null)
    {
        using var console = new ConsoleScope(input ?? string.Empty);

        var main = typeof(Program).GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Program.Main was not found.");
        var exitCode = await ((Task<int>)main.Invoke(null, [args])!).ConfigureAwait(false);

        return (exitCode, console.Out.ToString(), console.Error.ToString());
    }
}
