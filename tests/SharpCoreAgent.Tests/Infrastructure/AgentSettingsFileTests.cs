using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Infrastructure.Configuration;
using SharpCoreAgent.Tests.Cli;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The settings document: where it lives, what it accepts, and what it writes.
/// </summary>
/// <remarks>
/// The reader knows nothing about providers or approval modes — that is the parser's job — so these
/// cases are about bytes: a missing file, a malformed one, a sorted and reproducible save, and the four
/// places the path can come from.
/// </remarks>
public sealed class AgentSettingsFileTests
{
    [Fact]
    public void AMissingFileIsTheEmptyDocument()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "config.json");

        Assert.Empty(AgentSettingsFile.Read(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ASaveRoundTripsAndIsReproducible()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "nested", "config.json");

        AgentSettingsFile.Save(path, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = "grok",
            ["approval"] = "readonly",
        });

        var text = File.ReadAllText(path);
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("\"approval\"", StringComparison.Ordinal) < text.IndexOf("\"provider\"", StringComparison.Ordinal),
            "keys are sorted, so a second save of the same settings is byte-identical");
        var values = AgentSettingsFile.Read(path);
        Assert.Equal("grok", values["provider"]);
        Assert.Equal("readonly", values["approval"]);
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public void ASaveOverwritesWithoutLeavingATemporaryFile()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "config.json");

        AgentSettingsFile.Save(path, new Dictionary<string, string>(StringComparer.Ordinal) { ["provider"] = "grok" });
        AgentSettingsFile.Save(path, new Dictionary<string, string>(StringComparer.Ordinal) { ["provider"] = "demo" });

        Assert.Equal("demo", AgentSettingsFile.Read(path)["provider"]);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void MalformedJsonNamesThePathAndTheLine()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(path, "{" + Environment.NewLine + "  \"provider\": \"grok\"," + Environment.NewLine + "}");

        var error = Assert.Throws<ConfigurationFileException>(() => AgentSettingsFile.Read(path));

        Assert.Contains(path, error.Message, StringComparison.Ordinal);
        Assert.Contains("line", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADirectoryIsNotAFile()
    {
        using var directory = TempDirectory.Create();

        var error = Assert.Throws<ConfigurationFileException>(() => AgentSettingsFile.Read(directory.Path));

        Assert.Contains("is a directory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyFileIsTheEmptyDocument()
    {
        using var directory = TempDirectory.Create();
        var path = Path.Combine(directory.Path, "config.json");
        File.WriteAllText(path, "   ");

        Assert.Empty(AgentSettingsFile.Read(path));
    }

    [Fact]
    public void TheEnvironmentVariableWinsOverEveryPlatformDefault()
    {
        using var directory = TempDirectory.Create();
        var configured = Path.Combine(directory.Path, "mine.json");

        var path = AgentSettingsFile.ResolvePath(
            configured,
            Path.Combine(directory.Path, "xdg"),
            Path.Combine(directory.Path, "appdata"),
            directory.Path,
            isWindows: true);

        Assert.Equal(Path.GetFullPath(configured), path);
    }

    [Fact]
    public void WindowsUsesTheRoamingApplicationData()
    {
        using var directory = TempDirectory.Create();
        var appData = Path.Combine(directory.Path, "appdata");

        var path = AgentSettingsFile.ResolvePath(
            null,
            Path.Combine(directory.Path, "xdg"),
            appData,
            directory.Path,
            isWindows: true);

        Assert.Equal(Path.Combine(Path.GetFullPath(appData), "sharpcoreagent", "config.json"), path);
    }

    [Fact]
    public void UnixHonoursXdgAndFallsBackToTheHomeDirectory()
    {
        using var directory = TempDirectory.Create();
        var xdg = Path.Combine(directory.Path, "xdg");

        Assert.Equal(
            Path.Combine(Path.GetFullPath(xdg), "sharpcoreagent", "config.json"),
            AgentSettingsFile.ResolvePath(null, xdg, null, directory.Path, isWindows: false));

        Assert.Equal(
            Path.Combine(Path.GetFullPath(directory.Path), ".config", "sharpcoreagent", "config.json"),
            AgentSettingsFile.ResolvePath(null, null, null, directory.Path, isWindows: false));
    }

    [Fact]
    public void TheProcessPathReadsTheVariable()
    {
        using var directory = TempDirectory.Create();
        var configured = Path.Combine(directory.Path, "config.json");

        var path = AgentSettingsFile.PathFromEnvironment(name =>
            name == AgentSettingsFile.EnvironmentVariable ? configured : null);

        Assert.Equal(Path.GetFullPath(configured), path);
    }
}
