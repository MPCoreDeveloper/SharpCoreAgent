using SharpCoreAgent.Cli;
using SharpCoreAgent.Infrastructure.Configuration;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The wizard behind <c>sharpcoreagent setup</c>: what it asks, what it refuses to write, and what a
/// script gets instead of a conversation.
/// </summary>
/// <remarks>
/// The command takes its input and its terminal probe as arguments, so every case here is a script of
/// keystrokes rather than a redirect of the process console: the questions, the re-asks and the file are
/// asserted on the same run a developer would have had.
/// </remarks>
public sealed class SetupCommandTests
{
    [Fact]
    public void WithoutATerminalItPrintsTheTableInstead()
    {
        using var directory = TempDirectory.Create();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var input = new StringReader("grok\n");

        var exitCode = SetupCommand.Execute(
            Options(),
            Path.Combine(directory.Path, "config.json"),
            NoEnvironment,
            output,
            error,
            input,
            () => false);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("approval", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("needs a terminal", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("config set", error.ToString(), StringComparison.Ordinal);

        // The answers were never read: a fallback that consumed them would eat a script's next line.
        Assert.Equal('g', (char)input.Read());
    }

    [Fact]
    public void TheAnswersAreWrittenToTheFile()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "config.json");
        using var output = new StringWriter();

        var exitCode = SetupCommand.Execute(
            Options(),
            path,
            NoEnvironment,
            output,
            TextWriter.Null,
            new StringReader("grok\n\n\nallow\nplan\n"),
            () => true);

        Assert.Equal(ExitCodes.Success, exitCode);

        var document = AgentSettingsFile.Read(path);
        Assert.Equal("grok", document["provider"]);
        Assert.Equal("allow", document["approval"]);
        Assert.Equal("plan", document["mode"]);

        // Enter means "keep", not "pin the default": a wizard that wrote every default would turn one
        // visit into a file that no longer follows the build's defaults.
        Assert.False(document.ContainsKey("model"));
        Assert.False(document.ContainsKey("credential-file"));
        Assert.Contains("wrote 3 setting(s)", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("next:", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARejectedAnswerIsAskedAgainInsteadOfEndingTheWizard()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "config.json");
        using var output = new StringWriter();

        // Everything kept except approval: the first answer is not a mode, the second one is.
        var exitCode = SetupCommand.Execute(
            Options(),
            path,
            NoEnvironment,
            output,
            TextWriter.Null,
            new StringReader("\n\n\nsometimes\nallow\n\n"),
            () => true);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("unknown approval mode", output.ToString(), StringComparison.Ordinal);
        Assert.Equal("allow", AgentSettingsFile.Read(path)["approval"]);
    }

    [Fact]
    public void EverythingKeptWritesNothing()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "config.json");
        using var output = new StringWriter();

        var exitCode = SetupCommand.Execute(
            Options(),
            path,
            NoEnvironment,
            output,
            TextWriter.Null,
            new StringReader("\n\n\n\n\n"),
            () => true);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.False(File.Exists(path));
        Assert.Contains("nothing changed", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbandonedWizardWritesNothing()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "config.json");
        using var output = new StringWriter();

        // The provider was answered, then the input ended: half a configuration is worse than none.
        var exitCode = SetupCommand.Execute(
            Options(),
            path,
            NoEnvironment,
            output,
            TextWriter.Null,
            new StringReader("grok\n"),
            () => true);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.False(File.Exists(path));
        Assert.Contains("nothing was written", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFileKeepsTheSettingsTheWizardDidNotAskAbout()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "config.json");
        AgentSettingsFile.Save(path, new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["temperature"] = "0.5",
            ["approval"] = "readonly",
        });

        _ = SetupCommand.Execute(
            Options(),
            path,
            NoEnvironment,
            TextWriter.Null,
            TextWriter.Null,
            new StringReader("\n\n\nallow\n\n"),
            () => true);

        var document = AgentSettingsFile.Read(path);
        Assert.Equal("0.5", document["temperature"]);
        Assert.Equal("allow", document["approval"]);
    }

    private static CliOptions Options() => CliOptions.Parse(["setup", "--provider", "demo"], NoEnvironment);

    private static string? NoEnvironment(string name) => null;
}
