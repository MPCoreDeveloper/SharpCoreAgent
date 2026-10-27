using SharpCoreAgent.Cli;
using SharpCoreAgent.Domain.Errors;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// <c>sharpcoreagent config</c>: the five verbs, the layer each value comes from, and the two things it
/// refuses to do.
/// </summary>
/// <remarks>
/// The command is exercised directly with an explicit path and an explicit environment reader, so no
/// test depends on where the machine keeps its own configuration or on which provider key happens to be
/// exported in the shell running the suite.
/// </remarks>
[Collection(CliConsole.Name)]
public sealed class ConfigCommandTests
{
    [Fact]
    public void TheBareCommandIsShow()
    {
        var options = CliOptions.Parse(["config"], NoEnvironment, NotInteractive);

        Assert.Equal(CliCommand.Config, options.Command);
        Assert.Equal(ConfigAction.Show, options.ConfigAction);
    }

    [Fact]
    public void EveryVerbIsParsed()
    {
        Assert.Equal(ConfigAction.Show, Parse(["config", "show"]).ConfigAction);
        Assert.Equal(ConfigAction.Path, Parse(["config", "path"]).ConfigAction);

        var get = Parse(["config", "get", "provider"]);
        Assert.Equal(ConfigAction.Get, get.ConfigAction);
        Assert.Equal("provider", get.ConfigName);

        var set = Parse(["config", "set", "approval", "readonly"]);
        Assert.Equal(ConfigAction.Set, set.ConfigAction);
        Assert.Equal("approval", set.ConfigName);
        Assert.Equal("readonly", set.ConfigValue);

        var unset = Parse(["config", "unset", "approval"]);
        Assert.Equal(ConfigAction.Unset, unset.ConfigAction);
        Assert.Equal("approval", unset.ConfigName);
    }

    [Fact]
    public void ANameIsFoldedOntoItsSetting()
    {
        Assert.Equal("max-iterations", Parse(["config", "get", "maxIterations"]).ConfigName);
        Assert.Equal("max-iterations", Parse(["config", "get", "MAX_ITERATIONS"]).ConfigName);
        Assert.Equal("base-url", Parse(["config", "set", "Base-Url", "http://x"]).ConfigName);
    }

    [Fact]
    public void AnUnknownVerbIsAUsageError()
    {
        var error = Assert.Throws<CliUsageException>(() => Parse(["config", "wat"]));

        Assert.Contains("'wat' is not a config action", error.Message, StringComparison.Ordinal);
        Assert.Contains("show, get, set, unset and path", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownSettingIsAUsageErrorThatListsTheSettings()
    {
        var error = Assert.Throws<CliUsageException>(() => Parse(["config", "set", "bogus", "1"]));

        Assert.Contains("'bogus' is not a setting", error.Message, StringComparison.Ordinal);
        Assert.Contains("credential-file", error.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ACredentialIsRefusedWithTheRouteToTheRightFile()
    {
        // The settings file is copied and committed; the key has its own file. The refusal has to say so,
        // because "api-key is not a setting" would send the developer looking for the right spelling.
        var error = Assert.Throws<CliUsageException>(() => Parse(["config", "set", "api-key", "sk-x"]));

        Assert.Contains("credential", error.Message, StringComparison.Ordinal);
        Assert.Contains("--credential-file", error.Hint, StringComparison.Ordinal);

        Assert.Throws<CliUsageException>(() => Parse(["config", "get", "DEEPSEEK_API_KEY"]));
        Assert.Throws<CliUsageException>(() => Parse(["config", "get", "xai_api_key"]));
    }

    [Fact]
    public void MissingOperandsAreUsageErrors()
    {
        Assert.Contains(
            "needs a setting and a value",
            Assert.Throws<CliUsageException>(() => Parse(["config", "set", "approval"])).Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "needs one setting name",
            Assert.Throws<CliUsageException>(() => Parse(["config", "get"])).Message,
            StringComparison.Ordinal);
        Assert.Throws<CliUsageException>(() => Parse(["config", "show", "extra"]));
    }

    [Fact]
    public void JsonIsRefusedBecauseOneValueHasItsOwnVerb()
    {
        var error = Assert.Throws<CliUsageException>(() => Parse(["config", "--json"]));

        Assert.Contains("only valid with run and index", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowNamesTheLayerEachValueCameFrom()
    {
        using var directory = TempDirectory.Create();
        var (cli, path) = Load(
            directory,
            """{"approval":"readonly","provider":"grok"}""",
            ["config", "--mode", "plan"],
            Env(("SHARPCOREAGENT_MODEL", "grok-4.7")));

        var output = new StringWriter();
        Assert.Equal(0, ConfigCommand.Execute(cli, path, Env(("SHARPCOREAGENT_MODEL", "grok-4.7")), output));
        var table = output.ToString();

        Assert.Contains("file", Row(table, "approval"), StringComparison.Ordinal);
        Assert.Contains("readonly", Row(table, "approval"), StringComparison.Ordinal);
        Assert.Contains("environment", Row(table, "model"), StringComparison.Ordinal);
        Assert.Contains("grok-4.7", Row(table, "model"), StringComparison.Ordinal);
        Assert.Contains("command line", Row(table, "mode"), StringComparison.Ordinal);
        Assert.Contains("plan", Row(table, "mode"), StringComparison.Ordinal);
        Assert.Contains("default", Row(table, "temperature"), StringComparison.Ordinal);
        Assert.Contains(path, table, StringComparison.Ordinal);
    }

    [Fact]
    public void SetWritesTheFileWithSortedKeys()
    {
        using var directory = TempDirectory.Create();
        var (cli, path) = Load(directory, json: null, ["config", "set", "provider", "demo"]);

        var output = new StringWriter();
        Assert.Equal(0, ConfigCommand.Execute(cli, path, NoEnvironment, output));
        Assert.Contains("set provider", output.ToString(), StringComparison.Ordinal);

        var (approval, _) = Load(directory, json: null, ["config", "set", "approval", "readonly"]);
        Assert.Equal(0, ConfigCommand.Execute(approval, path, NoEnvironment, new StringWriter()));

        var text = File.ReadAllText(path);
        Assert.Contains("\"provider\": \"demo\"", text, StringComparison.Ordinal);
        Assert.Contains("\"approval\": \"readonly\"", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("\"approval\"", StringComparison.Ordinal) < text.IndexOf("\"provider\"", StringComparison.Ordinal),
            "keys are written in sorted order so a diff says only what changed");
    }

    [Fact]
    public void SetRefusesAValueOutOfRangeAndWritesNothing()
    {
        using var directory = TempDirectory.Create();
        var (cli, path) = Load(directory, json: null, ["config", "set", "temperature", "5"]);

        var error = Assert.Throws<CliUsageException>(
            () => ConfigCommand.Execute(cli, path, NoEnvironment, new StringWriter()));

        Assert.Contains("temperature", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void UnsetRemovesTheKeyAndSaysWhenThereIsNone()
    {
        using var directory = TempDirectory.Create();
        var (first, path) = Load(directory, """{"approval":"readonly"}""", ["config", "unset", "approval"]);

        var output = new StringWriter();
        Assert.Equal(0, ConfigCommand.Execute(first, path, NoEnvironment, output));
        Assert.Contains("removed approval", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("approval", File.ReadAllText(path), StringComparison.Ordinal);

        var (again, _) = Load(directory, json: null, ["config", "unset", "approval"]);
        var second = new StringWriter();
        Assert.Equal(0, ConfigCommand.Execute(again, path, NoEnvironment, second));
        Assert.Contains("is not set", second.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GetPrintsOneValueAndNothingElse()
    {
        using var directory = TempDirectory.Create();
        var (cli, path) = Load(directory, """{"approval":"readonly"}""", ["config", "get", "approval"]);

        var output = new StringWriter();
        Assert.Equal(0, ConfigCommand.Execute(cli, path, NoEnvironment, output));

        Assert.Equal("readonly" + System.Environment.NewLine, output.ToString());
    }

    [Fact]
    public void PathPrintsTheFileWhetherOrNotItExists()
    {
        using var directory = TempDirectory.Create();
        var (cli, path) = Load(directory, json: null, ["config", "path"]);

        var output = new StringWriter();
        Assert.Equal(0, ConfigCommand.Execute(cli, path, NoEnvironment, output));

        Assert.Equal(path + System.Environment.NewLine, output.ToString());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AFileThatIsNotJsonIsReportedWithItsPathAndLine()
    {
        using var directory = TempDirectory.Create();
        var path = Write(directory, "{ not json");

        var parse = Assert.Throws<ConfigurationFileException>(
            () => CliOptions.Parse(["config"], NoEnvironment, NotInteractive, () => CliSettings.Load(path)));

        Assert.Contains(path, parse.Message, StringComparison.Ordinal);
        Assert.Contains("line", parse.Message, StringComparison.Ordinal);
        Assert.NotNull(parse.InnerException);
    }

    [Fact]
    public void AValueTheFlagWouldRejectIsReportedWithItsKey()
    {
        using var directory = TempDirectory.Create();
        var path = Write(directory, """{"approval":"sometimes"}""");

        var parse = Assert.Throws<ConfigurationFileException>(
            () => CliOptions.Parse(["config"], NoEnvironment, NotInteractive, () => CliSettings.Load(path)));

        Assert.Contains(path, parse.Message, StringComparison.Ordinal);
        Assert.Contains("approval", parse.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetRepairsAFileThatCannotBeApplied()
    {
        // The reason `set` never applies the file: a document with an unusable value is exactly the
        // document the developer needs this command to fix.
        using var directory = TempDirectory.Create();
        var (cli, path) = Load(
            directory,
            """{"approval":"sometimes"}""",
            ["config", "set", "approval", "ask"]);

        Assert.Equal(0, ConfigCommand.Execute(cli, path, NoEnvironment, new StringWriter()));

        var repaired = CliOptions.Parse(
            ["config", "get", "approval"],
            NoEnvironment,
            NotInteractive,
            () => CliSettings.Load(path));
        Assert.Equal("approval", repaired.ConfigName);
        Assert.Equal("ask", ConfigCommandValue(repaired));
    }

    [Fact]
    public void UnsetRemovesAKeyThisBuildDoesNotKnow()
    {
        // A key a newer build wrote must not make the file unrepairable by the older one.
        using var directory = TempDirectory.Create();
        var (cli, path) = Load(directory, """{"bogus":"1"}""", ["config", "unset", "bogus"]);

        Assert.Equal(0, ConfigCommand.Execute(cli, path, NoEnvironment, new StringWriter()));
        Assert.DoesNotContain("bogus", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the effective value of the setting a parsed <c>config get</c> names, through the command.
    /// </summary>
    /// <param name="cli">The parsed command line.</param>
    /// <returns>The value without its newline.</returns>
    private static string ConfigCommandValue(CliOptions cli)
    {
        var output = new StringWriter();
        ConfigCommand.Execute(cli, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "absent.json"), NoEnvironment, output);
        return output.ToString().TrimEnd('\r', '\n');
    }

    private static string Row(string table, string name) =>
        Assert.Single(
            table.Split(System.Environment.NewLine),
            line => line.StartsWith(name + " ", StringComparison.Ordinal));

    private static (CliOptions Options, string Path) Load(
        TempDirectory directory,
        string? json,
        string[] args,
        Func<string, string?>? environment = null)
    {
        var path = System.IO.Path.Combine(directory.Path, "config.json");
        if (json is not null)
        {
            path = Write(directory, json);
        }

        var options = CliOptions.Parse(args, environment ?? NoEnvironment, NotInteractive, () => CliSettings.Load(path));
        return (options, path);
    }

    private static string Write(TempDirectory directory, string json)
    {
        var path = System.IO.Path.Combine(directory.Path, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] pairs) =>
        name => pairs.Where(pair => string.Equals(pair.Name, name, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .FirstOrDefault();

    private static CliOptions Parse(string[] args) => CliOptions.Parse(args, NoEnvironment, NotInteractive);

    private static bool NotInteractive() => false;

    private static string? NoEnvironment(string name) => null;
}
