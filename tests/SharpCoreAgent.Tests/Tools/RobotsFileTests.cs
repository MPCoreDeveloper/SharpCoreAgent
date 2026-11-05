using SharpCoreAgent.Infrastructure.Web;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// The custom robots parser: no dependency, but the matching rules still have to be predictable.
/// </summary>
public sealed class RobotsFileTests
{
    [Fact]
    public void AnEmptyFileAllowsEverything()
    {
        Assert.True(RobotsFile.Empty.Allows("/anything"));
        Assert.True(RobotsFile.Parse(string.Empty, "sharpcoreagent/1.0").Allows("/anything"));
    }

    [Fact]
    public void TheLongestMatchingRuleWins()
    {
        var rules = RobotsFile.Parse(
            """
            User-agent: *
            Disallow: /
            Allow: /public/
            """,
            "sharpcoreagent/1.0");

        Assert.False(rules.Allows("/private"));
        Assert.True(rules.Allows("/public/page"));
    }

    [Fact]
    public void AProductGroupOverridesTheStarGroup()
    {
        var rules = RobotsFile.Parse(
            """
            User-agent: *
            Disallow: /

            User-agent: sharpcoreagent
            Allow: /
            """,
            "sharpcoreagent/1.0");

        Assert.True(rules.Allows("/page"));
    }

    [Fact]
    public void AnEmptyDisallowAllowsTheWholeGroup()
    {
        var rules = RobotsFile.Parse(
            """
            User-agent: *
            Disallow:
            """,
            "sharpcoreagent/1.0");

        Assert.True(rules.Allows("/page"));
    }
}
