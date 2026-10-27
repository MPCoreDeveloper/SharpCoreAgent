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
    public void TheExitCodeTableDocumentsTheIndexFailure()
    {
        var row = Assert.Single(
            HelpText.Usage.Split('\n'),
            line => line.TrimStart().StartsWith("6 ", StringComparison.Ordinal));

        Assert.Contains("the index", row, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExitCodeTableRunsFromZeroToSix()
    {
        for (var code = 0; code <= 6; code++)
        {
            var row = string.Create(CultureInfo.InvariantCulture, $"\n  {code}  ");
            Assert.Contains(row, HelpText.Usage, StringComparison.Ordinal);
        }

        var seven = "\n  7  ";
        Assert.DoesNotContain(seven, HelpText.Usage, StringComparison.Ordinal);
    }
}