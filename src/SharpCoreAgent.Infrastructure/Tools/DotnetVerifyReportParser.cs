using System.Globalization;
using System.Text.RegularExpressions;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Reads a <c>dotnet build</c> or <c>dotnet test</c> log into the facts a verify result states.
/// </summary>
/// <remarks>
/// The log is the SDK's, not this repository's, so the parser reads a handful of shapes and defaults to the
/// exit code for everything else: a build whose wording changed still reports the right verdict, it only loses
/// the summary line. <c>dotnet_verify</c> asks the child for English (<c>DOTNET_CLI_UI_LANGUAGE=en</c>) because
/// the markers below are English words; a localized SDK would otherwise answer "exit code 1" with no summary.
/// </remarks>
public static partial class DotnetVerifyReportParser
{
    /// <summary>Gets the most diagnostic lines a report carries.</summary>
    public const int MaxReportedErrors = 25;

    /// <summary>
    /// Parses one log.
    /// </summary>
    /// <param name="exitCode">The process exit code, which decides the verdict when the log says nothing.</param>
    /// <param name="output">Everything the run printed, stdout and stderr interleaved.</param>
    /// <returns>The report.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
    public static DotnetVerifyReport Parse(int exitCode, string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var errors = new List<string>();
        var warningLines = 0;
        int? warningTotal = null;
        int? testPassed = null;
        int? testFailed = null;
        string? summary = null;
        var failed = false;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0)
            {
                continue;
            }

            summary ??= VerdictLine(line);
            failed |= line.StartsWith("Build FAILED.", StringComparison.Ordinal) ||
                      line.StartsWith("Failed!", StringComparison.Ordinal);

            if (ErrorPattern().IsMatch(line))
            {
                AddError(errors, line);
            }
            else if (WarningPattern().IsMatch(line))
            {
                warningLines++;
            }

            var total = WarningTotalPattern().Match(line);
            if (total.Success && int.TryParse(total.Groups["count"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var warnings))
            {
                // The last total wins: a multi-project build prints one per project and one for the whole run.
                warningTotal = warnings;
            }

            var tests = TestCountPattern().Match(line);
            if (tests.Success)
            {
                testFailed = Parse(tests.Groups["failed"].Value);
                testPassed = Parse(tests.Groups["passed"].Value);
            }
        }

        return new DotnetVerifyReport
        {
            Succeeded = exitCode == 0 && !failed && errors.Count == 0 && testFailed is not > 0,
            ExitCode = exitCode,
            Summary = summary ?? string.Create(CultureInfo.InvariantCulture, $"exit code {exitCode}"),
            WarningCount = warningTotal ?? warningLines,
            Errors = errors,
            TestsPassed = testPassed,
            TestsFailed = testFailed,
        };
    }

    /// <summary>
    /// Recognises the one line per run that says how it went.
    /// </summary>
    /// <param name="line">A trimmed log line.</param>
    /// <returns>The line when it is a verdict, otherwise <see langword="null"/>.</returns>
    private static string? VerdictLine(string line) =>
        line.StartsWith("Build succeeded.", StringComparison.Ordinal) ||
        line.StartsWith("Build FAILED.", StringComparison.Ordinal) ||
        line.StartsWith("Passed!", StringComparison.Ordinal) ||
        line.StartsWith("Failed!", StringComparison.Ordinal)
            ? line
            : null;

    private static void AddError(List<string> errors, string line)
    {
        // MSBuild prints each diagnostic once where it happened and again in the summary, so the same text
        // arrives twice. A header that says "2 errors" when there is one problem is worse than no header.
        if (errors.Count < MaxReportedErrors && !errors.Contains(line, StringComparer.Ordinal))
        {
            errors.Add(line);
        }
    }

    private static int Parse(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;

    [GeneratedRegex(@"\berror\s+[A-Z]+\d+\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorPattern();

    [GeneratedRegex(@"\bwarning\s+[A-Z]+\d+\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex WarningPattern();

    [GeneratedRegex(@"^\s*(?<count>\d+)\s+Warning\(s\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex WarningTotalPattern();

    [GeneratedRegex(@"Failed:\s*(?<failed>\d+),\s*Passed:\s*(?<passed>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex TestCountPattern();
}
