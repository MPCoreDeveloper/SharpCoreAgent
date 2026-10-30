using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// The parser is what turns the SDK's prose into the facts a verify result states, so each case pins one shape of
/// log rather than the whole report.
/// </summary>
/// <remarks>
/// No test here starts a compiler: the log is a string, which is the point of the split between
/// <see cref="DotnetVerifyReportParser"/> and the tool that runs the process. The two shapes that matter most are
/// the ones a naive parser gets wrong — a diagnostic printed twice, and a test run that reports a failure while
/// exiting 0.
/// </remarks>
public sealed class DotnetVerifyReportParserTests
{
    [Fact]
    public void APassingBuildSucceedsAndKeepsItsWarningTotal()
    {
        var report = DotnetVerifyReportParser.Parse(
            0,
            """
            Build succeeded.
                0 Warning(s)
                0 Error(s)
            """);

        Assert.True(report.Succeeded);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal("Build succeeded.", report.Summary);
        Assert.Equal(0, report.WarningCount);
        Assert.Empty(report.Errors);
        Assert.Null(report.TestsPassed);
    }

    [Fact]
    public void ADiagnosticIsReportedOnceEvenWhenTheLogPrintsItTwice()
    {
        // MSBuild prints the error where it happened and again in the summary, which is exactly how a header
        // ends up claiming two problems where there is one.
        const string Error = "src/App/Program.cs(7,9): error CS0103: The name 'Foo' does not exist";

        var report = DotnetVerifyReportParser.Parse(
            1,
            $"""
            {Error}

            Build FAILED.

            {Error}
                1 Warning(s)
                1 Error(s)
            """);

        Assert.False(report.Succeeded);
        Assert.Equal("Build FAILED.", report.Summary);
        Assert.Equal(Error, Assert.Single(report.Errors));
        Assert.Equal(1, report.WarningCount);
    }

    [Fact]
    public void ATestRunCarriesItsCountsAndFailsOnAFailedTest()
    {
        var report = DotnetVerifyReportParser.Parse(
            1,
            "Failed!  - Failed:     2, Passed:    10, Skipped:     0, Total:    12, Duration: 130 ms");

        Assert.False(report.Succeeded);
        Assert.Equal(10, report.TestsPassed);
        Assert.Equal(2, report.TestsFailed);
        Assert.StartsWith("Failed!", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ATestSummaryThatExitedZeroStillCountsAFailure()
    {
        // A runner that reports a failure and exits 0 is a real thing; the exit code alone would call this a pass.
        var report = DotnetVerifyReportParser.Parse(
            0,
            "Failed!  - Failed:     1, Passed:    11, Skipped:     0, Total:    12, Duration: 130 ms");

        Assert.False(report.Succeeded);
        Assert.Equal(1, report.TestsFailed);
    }

    [Fact]
    public void ASilentRunAnswersWithTheExitCode()
    {
        var report = DotnetVerifyReportParser.Parse(3, string.Empty);

        Assert.False(report.Succeeded);
        Assert.Equal("exit code 3", report.Summary);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public void ALocalizedLogFallsBackToTheExitCodeInsteadOfInventingAVerdict()
    {
        // dotnet_verify asks the child for English precisely so this does not happen; the fallback is what keeps a
        // machine whose SDK ignored the request from reporting a pass.
        var report = DotnetVerifyReportParser.Parse(1, "Fout(en)\n    0 Waarschuwing(en)\n    1 Fout(en)");

        Assert.False(report.Succeeded);
        Assert.Equal("exit code 1", report.Summary);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public void WarningsAreCountedWhenTheLogPrintsNoTotal()
    {
        var report = DotnetVerifyReportParser.Parse(
            0,
            """
            src/A.cs(3,5): warning CS8618: Non-nullable field must contain a non-null value.
            src/B.cs(8,9): warning CS0168: The variable is declared but never used.
            Build succeeded.
            """);

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.WarningCount);
    }

    [Fact]
    public void TheErrorListIsCappedAndTheRestStaysInTheLog()
    {
        var log = string.Join(
            '\n',
            Enumerable.Range(0, 40).Select(index => $"src/F{index}.cs(1,1): error CS000{index}: boom {index}"));

        var report = DotnetVerifyReportParser.Parse(1, log);

        Assert.Equal(DotnetVerifyReportParser.MaxReportedErrors, report.Errors.Count);
        Assert.StartsWith("src/F0.cs", report.Errors[0], StringComparison.Ordinal);
    }
}
