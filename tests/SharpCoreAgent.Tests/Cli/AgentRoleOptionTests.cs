using SharpCoreAgent.Cli;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Infrastructure.Providers;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The three roles a session can hand to a different model: what the flags accept, what each layer may
/// supply, and which model a plan turn, an act turn and a review run on.
/// </summary>
/// <remarks>
/// The parser is pure here too, so every case is a few strings in and a descriptor out. What the loop does
/// with the answer is <see cref="ChatRoleTests"/>'s business.
/// </remarks>
public sealed class AgentRoleOptionTests
{
    [Fact]
    public void ARoleFlagNamesAModelForEachJob()
    {
        var options = CliOptions.Parse(
            ["chat", "--planner", "grok:grok-4.7", "--executor", "deepseek-flash", "--reviewer", "r1"],
            NoEnvironment);

        Assert.Equal("grok:grok-4.7", options.PlannerModel);
        Assert.Equal("deepseek-flash", options.ExecutorModel);
        Assert.Equal("r1", options.ReviewerModel);

        Assert.Equal("grok:grok-4.7", AgentRoles.For(options, AgentRole.Planner)!.QualifiedName);

        // A bare identifier keeps the provider, and an alias resolves through the catalogue.
        Assert.Equal("deepseek:deepseek-flash", AgentRoles.For(options, AgentRole.Executor)!.QualifiedName);
        Assert.Equal("deepseek:deepseek-reasoner", AgentRoles.For(options, AgentRole.Reviewer)!.QualifiedName);
    }

    [Fact]
    public void NoRoleNamedMeansTheSessionModel()
    {
        var options = CliOptions.Parse(["chat", "--provider", "demo"], NoEnvironment);
        var session = ModelCatalog.DescriptorFor("demo", null);

        Assert.Null(options.PlannerModel);
        Assert.All(AgentRoles.All, role => Assert.Null(AgentRoles.For(options, role)));
        Assert.Equal(session.QualifiedName, AgentRoles.ForMode(options, AgentMode.Plan, session).QualifiedName);
        Assert.Equal(session.QualifiedName, AgentRoles.ForMode(options, AgentMode.Act, session).QualifiedName);
    }

    [Fact]
    public void TheModeDecidesWhichRoleSuppliesTheModel()
    {
        var options = CliOptions.Parse(["chat", "--planner", "grok:grok-4.7", "--executor", "ds"], NoEnvironment);
        var session = ModelCatalog.DescriptorFor("demo", null);

        Assert.Equal("grok:grok-4.7", AgentRoles.ForMode(options, AgentMode.Plan, session).QualifiedName);
        Assert.Equal("deepseek:deepseek-flash", AgentRoles.ForMode(options, AgentMode.Act, session).QualifiedName);
    }

    [Fact]
    public void ABareIdentifierKeepsTheProviderWhereverTheFlagWasTyped()
    {
        // The provider may arrive after the role, so resolution waits until the line is parsed. A parser
        // that resolved in place would make these two lines mean different things.
        var before = CliOptions.Parse(["chat", "--planner", "grok-3", "--provider", "grok"], NoEnvironment);
        var after = CliOptions.Parse(["chat", "--provider", "grok", "--planner", "grok-3"], NoEnvironment);

        Assert.Equal("grok:grok-3", AgentRoles.For(before, AgentRole.Planner)!.QualifiedName);
        Assert.Equal("grok:grok-3", AgentRoles.For(after, AgentRole.Planner)!.QualifiedName);
    }

    [Fact]
    public void AnUnknownProviderInAPairIsAUsageError()
    {
        var error = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["chat", "--planner", "nope:model"], NoEnvironment));

        Assert.Contains("--planner", error.Message, StringComparison.Ordinal);
        Assert.Contains("no provider", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheThreeRolesAreSettingsWithTheirOwnNames()
    {
        Assert.Equal(["planner", "executor", "reviewer"], CliOptions.SettableNames.Skip(2).Take(3));
        Assert.Equal("planner", CliOptions.EnsureSettable("PLANNER"));
        Assert.Equal("executor", CliOptions.EnsureSettable("Executor"));

        var options = CliOptions.WithSetting(
            CliOptions.Parse(["chat"], NoEnvironment),
            "planner",
            "grok:grok-4.7");

        Assert.Equal("grok:grok-4.7", options.PlannerModel);
        Assert.Equal("SHARPCOREAGENT_PLANNER_MODEL", CliOptions.EnvironmentVariableFor("planner"));
        Assert.Equal("SHARPCOREAGENT_REVIEWER_MODEL", CliOptions.EnvironmentVariableFor("reviewer"));
    }

    [Fact]
    public void TheEnvironmentSuppliesTheRoles()
    {
        var options = CliOptions.Parse(
            ["chat"],
            Environment(("SHARPCOREAGENT_PLANNER_MODEL", "grok:grok-4.7")));

        Assert.Equal("grok:grok-4.7", options.PlannerModel);
        Assert.Null(options.ExecutorModel);
    }

    [Fact]
    public void EveryModelTheSessionMayUseIsListedOnceForThePreflight()
    {
        var options = CliOptions.Parse(
            ["chat", "--planner", "grok:grok-4.7", "--reviewer", "grok:grok-3", "--fallback", "grok:grok-4.7"],
            NoEnvironment);
        var session = ModelCatalog.DescriptorFor("deepseek", null);

        var models = AgentRoles.RequestModels(options, session);

        // The session's model, then the two named ones: the duplicate pair and the fallback are folded in.
        Assert.Equal(
            ["deepseek:deepseek-flash", "grok:grok-4.7", "grok:grok-3"],
            models.Select(model => model.QualifiedName));
    }

    [Fact]
    public void McpServeRefusesTheRoleFlags()
    {
        var error = Assert.Throws<CliUsageException>(() =>
            CliOptions.Parse(["mcp", "serve", "--planner", "grok:grok-4.7"], NoEnvironment));

        Assert.Contains("--planner", error.Message, StringComparison.Ordinal);
    }

    private static string? NoEnvironment(string name) => null;

    private static Func<string, string?> Environment(params (string Name, string Value)[] pairs) =>
        name => pairs.Where(pair => string.Equals(pair.Name, name, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .FirstOrDefault();
}
