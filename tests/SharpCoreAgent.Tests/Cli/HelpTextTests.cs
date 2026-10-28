using System.Globalization;
using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The text a developer reads before running anything, held to the promises the other contracts make.
/// </summary>
/// <remarks>
/// These are cheap assertions with an expensive failure mode: a command that exists but is not in the usage block is
/// a command nobody runs, and an exit code that is not in the table is one scripts learn by crashing into it.
/// </remarks>
public sealed class HelpTextTests
{
    [Fact]
    public void TheVersionLineNamesBothNamesInOrder() =>
        Assert.Matches(@"^Ariadne \d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)? \(SharpCoreAgent\)$", HelpText.VersionLine);

    [Fact]
    public void TheVersionCarriesNoCommitSuffix()
    {
        // SourceLink appends "+<hash>"; a bug report wants the version, so the suffix is cut in ReadVersion.
        Assert.DoesNotContain('+', HelpText.VersionLine);
        Assert.Equal($"Ariadne {HelpText.Version} (SharpCoreAgent)", HelpText.VersionLine);
    }

    [Fact]
    public void TheUsageBlockDocumentsTheIndexCommand()
    {
        Assert.Contains("sharpcoreagent index [options]", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("sharpcoreagent index --status --json", HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageBlockDocumentsEveryIndexOption()
    {
        string[] options =
        [
            "--status",
            "--rebuild",
            "--verify",
            "--prune",
            "--no-index",
            "--index-budget",
            "--embedder",
            "--tier",
        ];

        Assert.All(options, option => Assert.Contains(option, HelpText.Usage, StringComparison.Ordinal));
    }

    [Fact]
    public void TheChatCommandsDocumentTheCostReport()
    {
        // `/cost` prints a number a developer plans a session on, so the two qualifiers travel with it:
        // the table is dated, and it bills the peak rate where a provider has tiers.
        Assert.Contains("/cost", HelpText.ChatCommands, StringComparison.Ordinal);
        Assert.Contains("peak rate", HelpText.ChatCommands, StringComparison.Ordinal);
        Assert.Contains("dated", HelpText.ChatCommands, StringComparison.Ordinal);
        Assert.Contains("/help", HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageBlockDocumentsTheFallbackOption()
    {
        // The option only earns its keep if a developer can find it before the 401, so the statuses it
        // covers are part of the row rather than a footnote.
        Assert.Contains("--fallback", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("401", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("5xx", HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExitCodeTableDocumentsTheIndexFailure()
    {
        var row = Assert.Single(
            HelpText.Usage.Split('\n'),
            line => line.TrimStart().StartsWith("6 ", StringComparison.Ordinal));

        Assert.Contains("the index", row, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExitCodeTableRunsFromZeroToSeven()
    {
        for (var code = 0; code <= 7; code++)
        {
            var row = string.Create(CultureInfo.InvariantCulture, $"\n  {code}  ");
            Assert.Contains(row, HelpText.Usage, StringComparison.Ordinal);
        }

        var eight = "\n  8  ";
        Assert.DoesNotContain(eight, HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExitCodeTableDocumentsTheSettingsFailure()
    {
        var row = Assert.Single(
            HelpText.Usage.Split('\n'),
            line => line.TrimStart().StartsWith("7 ", StringComparison.Ordinal));

        Assert.Contains("settings file", row, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageBlockDocumentsTheBareStartAndTheConfigCommand()
    {
        Assert.Contains("sharpcoreagent [options]", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("sharpcoreagent config set <name> <value>", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("SHARPCOREAGENT_CONFIG", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("config set api-key", HelpText.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageBlockDocumentsTheCredentialFile()
    {
        // The option exists to remove a restart, so both halves are named: the flag a script passes and
        // the variable a developer sets once. Exit code 3 is where a bad file is reported, so its row
        // says so rather than leaving scripts to discover it.
        Assert.Contains("--credential-file", HelpText.Usage, StringComparison.Ordinal);
        Assert.Contains("SHARPCOREAGENT_CREDENTIAL_FILE", HelpText.Usage, StringComparison.Ordinal);

        var row = Assert.Single(
            HelpText.Usage.Split('\n'),
            line => line.TrimStart().StartsWith("3 ", StringComparison.Ordinal));

        Assert.Contains("credential file", row, StringComparison.Ordinal);
    }
}