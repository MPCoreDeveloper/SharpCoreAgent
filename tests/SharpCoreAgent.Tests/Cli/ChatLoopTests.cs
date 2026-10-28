using SharpCoreAgent.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// A conversation is more than one turn: the prompt has to come back after an answer, and after a turn
/// that ran a tool, because a session that answers once and then stops reading is indistinguishable from
/// a crash.
/// </summary>
/// <remarks>
/// Each case drives the real entry point with scripted input, so the loop, the observer, the transcript
/// write and the prompt are all the code a developer runs. The timeout is there to turn a hang — the
/// failure this file exists for — into a failed assertion instead of a stuck test run.
/// </remarks>
[Collection(CliConsole.Name)]
public sealed class ChatLoopTests
{
    /// <summary>How long one scripted conversation may take before the loop is declared stuck.</summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ThePromptComesBackAfterAnAnswer()
    {
        var (exitCode, output, _) = await ConverseAsync("hello\nand again\n/exit\n");

        Assert.Equal(0, exitCode);

        // Both prompts were answered: a loop that stalled after the first turn would show the first
        // echo and nothing else.
        Assert.Contains("hello", output, StringComparison.Ordinal);
        Assert.Contains("and again", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePromptComesBackAfterATurnThatRanATool()
    {
        var (exitCode, output, diagnostics) = await ConverseAsync("list the files\n/exit\n");

        Assert.Equal(0, exitCode);
        Assert.Contains("list_directory", output + diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavingWithExitEndsTheConversationCleanly()
    {
        var (exitCode, _, diagnostics) = await ConverseAsync("/exit\n");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("error:", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePromptComesBackWhenTheTranscriptIsSaved()
    {
        // The default path in a real session: every turn is followed by a transcript write, so a loop
        // that stalls here would look like an answer followed by silence.
        var (exitCode, output, _) = await ConverseAsync("hello\nand again\n/exit\n", saveSession: true);

        Assert.Equal(0, exitCode);
        Assert.Contains("hello", output, StringComparison.Ordinal);
        Assert.Contains("and again", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerboseNamesTheTurnSoAWaitIsNotSilence()
    {
        var (verboseExit, _, verboseDiagnostics) = await ConverseAsync("hello\n/exit\n", verbose: true);
        var (_, _, quietDiagnostics) = await ConverseAsync("hello\n/exit\n");

        Assert.Equal(0, verboseExit);
        Assert.Contains("turn started", verboseDiagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("turn started", quietDiagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlashApprovalReportsTheModeAndSaysHowLongAllowLasts()
    {
        var (exitCode, output, diagnostics) = await ConverseAsync("/approval\n/approval allow\n/exit\n");

        Assert.Equal(0, exitCode);

        // Bare /approval is a question, so its answer goes to the answer stream like /model and /mode.
        Assert.Contains("approval: ask", output, StringComparison.Ordinal);

        // The change is confirmed on the diagnostics stream, where the prompt and its scope note live.
        Assert.Contains("approval: allow", diagnostics, StringComparison.Ordinal);
        Assert.Contains("until this session exits", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlashBudgetReportsTheCeilingAndRaisesIt()
    {
        var (exitCode, output, _) = await ConverseAsync("/budget\n/budget +5000\n/exit\n");

        Assert.Equal(0, exitCode);

        // A developer has to be able to see the number that ends a session, and change it without losing the
        // transcript — the whole point of the budget living on the session rather than in the process.
        Assert.Contains("10,000,000 tokens", output, StringComparison.Ordinal);
        Assert.Contains("10,005,000 tokens", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlashBudgetRefusesACeilingTooSmallToAnswer()
    {
        var (exitCode, output, diagnostics) = await ConverseAsync("/budget 50\n/budget\n/exit\n");

        Assert.Equal(0, exitCode);

        // The command line refuses anything under 1 000 tokens, so the prompt has to as well: a ceiling that cannot
        // pay for one request would end every turn the developer then typed.
        Assert.Contains("the smallest ceiling is 1,000", diagnostics, StringComparison.Ordinal);
        Assert.Contains("10,000,000 tokens (0 spent)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NearingTheCeilingSaysSoBeforeTheSessionStops()
    {
        // Two demo answers bill about 1 200 tokens, which is most of a 1 400 token ceiling: the developer should hear
        // about it while there is still room to act, not when the prompt is already refusing them.
        var (exitCode, _, diagnostics) = await ConverseAsync("hello\nhello again\n/exit\n", budget: 1_400);

        Assert.Equal(0, exitCode);
        Assert.Contains("% of the session budget is spent", diagnostics, StringComparison.Ordinal);
        Assert.Contains("'/budget +1,400' raises it", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATurnThatFinishesOverTheCeilingSaysTheNextRequestNeedsMoreRoom()
    {
        // The answer that arrived is never thrown away for being one token over, so the warning is the only place
        // that can tell the developer their next request will be refused — and saying "88%" of an overdrawn budget
        // would read as a rounding note.
        var (exitCode, _, diagnostics) = await ConverseAsync("list the files\n/exit\n", budget: 1_000);

        Assert.Equal(0, exitCode);

        // The wording is the assertion; the exact figure is not. The demo adapter estimates tokens from the
        // characters it was handed, and those include the system prompt, so the count moves with the source's line
        // endings: the same turn reports 1,191 on a CRLF checkout and 1,179 on the LF one this repository's
        // .gitattributes prescribes. Pinning the number would make the test pass or fail on how a file was saved,
        // while what the warning has to prove is that an overdrawn budget is reported as spent, with the count over
        // the ceiling, rather than as a percentage of it.
        var spent = System.Text.RegularExpressions.Regex.Match(
            diagnostics,
            @"the session budget is spent \((?<spent>[\d,]+) of 1,000 tokens\)",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(5));

        Assert.True(spent.Success, diagnostics);
        Assert.True(
            long.Parse(spent.Groups["spent"].Value, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture) > 1_000,
            diagnostics);

        Assert.Contains("the next request needs '/budget +1,000'", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("% of the session budget", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReachedBudgetOffersMoreRoomAndContinuesTheSameRequest()
    {
        // The demo provider bills about 600 tokens for a temp workspace, so a 1 000 token ceiling is spent by the
        // second answer: the third request is where a session used to end. Answering the offer with "y" has to raise
        // the ceiling and replay the request, and the conversation has to survive it.
        var (exitCode, output, diagnostics) = await ConverseAsync("hello\nhello again\ny\ny\n/exit\n", budget: 1_000);

        Assert.Equal(0, exitCode);
        Assert.Contains("[Y/n]", diagnostics, StringComparison.Ordinal);
        Assert.Contains("budget: raised to 2,000 tokens", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Continue from where you stopped.", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A no at the budget offer used to end the conversation. It stops the request where it stands instead, because
    /// the work is still in the transcript and the developer's next keystroke should be able to pick it up.
    /// </summary>
    /// <param name="resume">The line that carries the request on: the command, or a bare Enter.</param>
    /// <returns>The pending test.</returns>
    [Theory]
    [InlineData("/continue")]
    [InlineData("")]
    public async Task ABudgetPauseWaitsForTheDeveloperInsteadOfEndingTheSession(string resume)
    {
        // "list the files" bills about 1 200 tokens in a temp workspace, so a 1 000 token ceiling is spent by the
        // first answer and the second request is refused before it starts: that refusal is the offer under test.
        // The second "n" is for the resumed request, which is refused again because no ceiling was raised.
        var (exitCode, _, diagnostics) = await ConverseAsync(
            $"list the files\nlist the files\nn\n{resume}\nn\n/exit\n",
            budget: 1_000);

        // Leaving with the request still paused is not a finished conversation, and a script has to be able to
        // tell the difference between that and a clean exit.
        Assert.Equal(ExitCodes.Budget, exitCode);

        // The hint and the paused prompt only exist because the loop stayed open and read another line: with the
        // old exit-on-no behaviour the second one would never be printed.
        Assert.Equal(2, Occurrences(diagnostics, "paused: the request is the last message"));
        Assert.Contains(" · paused ❯", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlashApprovalForgetRevokesNothingWhenNothingWasApprovedOnItsOwn()
    {
        var (exitCode, output, _) = await ConverseAsync("/approval\n/approval forget\n/exit\n");

        Assert.Equal(0, exitCode);

        // Revoking is the other half of a session-scoped yes, so the command has to answer even when the session
        // has never allowed a single tool: silence would read as "the command did not work".
        Assert.Contains("nothing to forget", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(this session only", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Counts how often a line appears in a stream, so a test can prove the loop took the same door twice.
    /// </summary>
    /// <param name="text">The captured stream.</param>
    /// <param name="value">The text to look for.</param>
    /// <returns>The number of occurrences.</returns>
    private static int Occurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    /// <summary>
    /// Types a script at a bare <c>sharpcoreagent</c> and returns what the process printed.
    /// </summary>
    /// <param name="script">One line per keystroke batch, ending with <c>/exit</c>.</param>
    /// <param name="saveSession">Whether the transcript is written after every turn, as it is by default.</param>
    /// <param name="verbose">Whether to ask for the per-turn marker.</param>
    /// <param name="budget">The session's token ceiling; zero leaves the default in place.</param>
    /// <returns>The exit code, the answer stream and the diagnostics stream.</returns>
    private static async Task<(int ExitCode, string Output, string Diagnostics)> ConverseAsync(
        string script,
        bool saveSession = false,
        bool verbose = false,
        long budget = 0)
    {
        using var directory = TempDirectory.Create();
        var previous = Terminal.Interactive;
        Terminal.Interactive = () => true;

        try
        {
            using var console = new ConsoleScope(script);

            var args = new List<string> { "--provider", "demo", "--workspace", directory.Path };
            if (!saveSession)
            {
                args.Add("--no-session");
            }

            if (verbose)
            {
                args.Add("--verbose");
            }

            if (budget > 0)
            {
                args.Add("--max-total-tokens");
                args.Add(budget.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var exitCode = await ProgramEntry.RunAsync([.. args]).WaitAsync(Ceiling);

            return (exitCode, console.Out.ToString(), console.Error.ToString());
        }
        finally
        {
            Terminal.Interactive = previous;
        }
    }
}
