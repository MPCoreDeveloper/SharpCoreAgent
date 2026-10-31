using SharpCoreAgent.Cli;
using SharpCoreAgent.Infrastructure.Configuration;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The roles inside a conversation: what <c>/roles</c> reports, what a mode switch does to the model, and
/// what <c>/review</c> runs.
/// </summary>
/// <remarks>
/// The real entry point with scripted input, as <see cref="ChatLoopTests"/> does, so the assertions are about
/// what a developer reads rather than about a helper returning a descriptor. Every run gets a credential file
/// holding a fake grok key: the preflight checks every named role before the first turn, and a test must not
/// depend on what the machine's own environment happens to export.
/// </remarks>
[Collection(CliConsole.Name)]
public sealed class ChatRoleTests
{
    [Fact]
    public async Task SlashRolesNamesEveryRoleAndSaysWhichAreTheSessionModel()
    {
        using var workspace = TempDirectory.Create();

        var (exitCode, output, _) = await ConverseAsync(workspace.Path, "/roles\n/exit\n");

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("plan", output, StringComparison.Ordinal);
        Assert.Contains("act", output, StringComparison.Ordinal);
        Assert.Contains("review", output, StringComparison.Ordinal);
        Assert.Equal(3, Occurrences(output, "(session model)"));
    }

    [Fact]
    public async Task AModeSwitchMovesToTheModelTheRoleNamesAndBackAgain()
    {
        using var workspace = TempDirectory.Create();

        var (exitCode, _, diagnostics) = await ConverseAsync(
            workspace.Path,
            "/mode plan\n/mode act\n/exit\n",
            ["--planner", "grok:grok-4.7"]);

        Assert.Equal(ExitCodes.Success, exitCode);

        // Plan runs on the planner's model, and says so on the diagnostics stream where the mode change is.
        Assert.Contains("model: grok:grok-4.7 (the planner model)", diagnostics, StringComparison.Ordinal);
        Assert.Contains("grok-4.7 · plan ❯", diagnostics, StringComparison.Ordinal);

        // Act named no executor, so it returns to the model the conversation started on rather than staying on
        // the planner's: a role wins only where it exists.
        Assert.Contains("model: demo:demo-scripted (the session model)", diagnostics, StringComparison.Ordinal);
        Assert.Contains("demo-scripted · act ❯", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARoleFromTheSettingsFileAppliesWithoutAFlag()
    {
        using var workspace = TempDirectory.Create();
        var settings = Path.Combine(workspace.Path, "config.json");
        AgentSettingsFile.Save(settings, new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["planner"] = "grok:grok-4.7",
        });

        var (exitCode, output, _) = await ConverseAsync(workspace.Path, "/roles\n/exit\n", settings: settings);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("grok:grok-4.7", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlashReviewRunsAReadOnlyTurnAndLeavesTheSessionAsItFoundIt()
    {
        using var workspace = TempDirectory.Create();

        var (exitCode, output, diagnostics) = await ConverseAsync(
            workspace.Path,
            "/review\n/approval\n/usage\n/exit\n",
            ["--approval", "allow"]);

        Assert.Equal(ExitCodes.Success, exitCode);

        // The header names the model and the fact that nothing in this turn can write, and the turn really ran:
        // the demo adapter answered by listing the workspace, which is the one tool a review keeps.
        Assert.Contains("review:", diagnostics, StringComparison.Ordinal);
        Assert.Contains("(read-only, in this transcript)", diagnostics, StringComparison.Ordinal);
        Assert.Contains("list_directory", output + diagnostics, StringComparison.Ordinal);
        Assert.Contains("turn(s)", output, StringComparison.Ordinal);

        // And the approval mode the session had is back: a review is one look, not a mode change. `allow` is
        // the mode that would have made a forgotten restore visible, which is why the session starts there.
        Assert.Contains("approval: allow", output, StringComparison.Ordinal);
        Assert.DoesNotContain("approval: readonly", output, StringComparison.Ordinal);
    }

    private static string? NoEnvironment(string name) => null;

    private static int Occurrences(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    /// <summary>
    /// Types a script into a session on the offline provider and returns what it printed.
    /// </summary>
    /// <param name="workspace">The directory the session runs in.</param>
    /// <param name="script">One line per keystroke batch, ending with <c>/exit</c>.</param>
    /// <param name="extra">Arguments after the provider, workspace and credential file.</param>
    /// <param name="settings">The settings file to pin, when the test writes one.</param>
    /// <returns>The exit code, the answer stream and the diagnostics stream.</returns>
    private static async Task<(int ExitCode, string Output, string Diagnostics)> ConverseAsync(
        string workspace,
        string script,
        string[]? extra = null,
        string? settings = null)
    {
        var keys = Path.Combine(workspace, "keys.env");
        await File.WriteAllTextAsync(
            keys,
            "XAI_API_KEY=sk-test\nGROK_API_KEY=sk-test\n",
            TestContext.Current.CancellationToken);

        var args = new List<string>
        {
            "chat", "--provider", "demo", "--workspace", workspace, "--no-session", "--credential-file", keys,
        };

        args.AddRange(extra ?? []);

        using var console = new ConsoleScope(script, settings);
        using var scope = new CancellationScope();

        // The loader is what `Program` passes: without it the parser never reads the settings file, and a test
        // about a role that comes from that file would prove nothing.
        var exitCode = await ChatCommand.ExecuteAsync(
            CliOptions.Parse(
                [.. args],
                NoEnvironment,
                loadSettings: settings is null ? null : () => CliSettings.Load(settings)),
            scope);

        return (exitCode, console.Out.ToString(), console.Error.ToString());
    }
}
