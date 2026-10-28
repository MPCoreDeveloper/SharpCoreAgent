using SharpCoreAgent.Cli;
using SharpCoreAgent.Infrastructure.Configuration;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// <c>/config</c> inside a conversation: the value is written to the same file <c>config set</c> writes,
/// and it takes effect in the session that asked for it.
/// </summary>
/// <remarks>
/// The loop is driven through the real entry point with scripted input, so the command parser, the
/// transcript save and the console split are the ones a developer gets. The settings file is pinned by
/// <see cref="ConsoleScope"/>, which is also what the loop resolves when it looks the path up.
/// </remarks>
[Collection(CliConsole.Name)]
public sealed class ChatConfigTests
{
    [Fact]
    public async Task SetWritesTheFileAndAppliesTheValue()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "config.json");
        using var console = new ConsoleScope(
            "/config set approval readonly\n/config path\n/config\n/config set nonsense 1\n/exit\n",
            path);
        using var scope = new CancellationScope();

        var exitCode = await ChatCommand.ExecuteAsync(Chat(directory.Path), scope);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("readonly", AgentSettingsFile.Read(path)["approval"]);
        Assert.Contains("config: approval = readonly", console.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains(path, console.Out.ToString(), StringComparison.Ordinal);

        // Bare /config is the wizard, which needs a terminal; a redirected stdin must be told so rather
        // than be asked a question nobody can answer.
        Assert.Contains("the wizard needs a terminal", console.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("'nonsense' is not a setting", console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetKeepsTheOtherSettingsInTheFile()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "config.json");
        AgentSettingsFile.Save(path, new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["temperature"] = "0.9",
        });

        using var console = new ConsoleScope("/config set mode plan\n/exit\n", path);
        using var scope = new CancellationScope();

        Assert.Equal(ExitCodes.Success, await ChatCommand.ExecuteAsync(Chat(directory.Path), scope));

        var document = AgentSettingsFile.Read(path);
        Assert.Equal("0.9", document["temperature"]);
        Assert.Equal("plan", document["mode"]);
    }

    [Fact]
    public async Task ShowPrintsTheTableWithoutChangingAnything()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "config.json");
        using var console = new ConsoleScope("/config show\n/exit\n", path);
        using var scope = new CancellationScope();

        Assert.Equal(ExitCodes.Success, await ChatCommand.ExecuteAsync(Chat(directory.Path), scope));

        Assert.Contains("max-total-tokens", console.Out.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    private static CliOptions Chat(string workspace) => CliOptions.Parse(
        ["chat", "--provider", "demo", "--workspace", workspace, "--no-session"],
        NoEnvironment);

    private static string? NoEnvironment(string name) => null;
}
