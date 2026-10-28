using SharpCoreAgent.Application;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Infrastructure.Providers;
using Xunit;

namespace SharpCoreAgent.Tests.Application;

/// <summary>
/// Usage is what the budget and <c>/usage</c> are made of, so a first turn that reports twice the tokens
/// the provider sent is not a cosmetic defect: it stops a session early and misreports the cost. The
/// totals were observed disagreeing with the per-model rows before these tests existed.
/// </summary>
public sealed class AgentSessionUsageTests
{
    private static AgentSession CreateSession() => new(
        Workspace.Create(Path.GetTempPath()),
        ModelCatalog.DescriptorFor("demo", null),
        ApprovalMode.Ask);

    [Fact]
    public void TheFirstRequestIsCountedOnce()
    {
        var session = CreateSession();

        session.RecordUsage("demo:demo-scripted", new TokenUsage(304, 30));

        Assert.Equal(304, session.TotalUsage.InputTokens);
        Assert.Equal(30, session.TotalUsage.OutputTokens);
        var row = Assert.Single(session.UsageByModel);
        Assert.Equal(304, row.InputTokens);
        Assert.Equal(30, row.OutputTokens);
        Assert.Equal(1, row.Turns);
    }

    [Fact]
    public void LaterRequestsAccumulateOnceEach()
    {
        var session = CreateSession();

        session.RecordUsage("demo:demo-scripted", new TokenUsage(100, 10));
        session.RecordUsage("demo:demo-scripted", new TokenUsage(200, 20));

        Assert.Equal(300, session.TotalUsage.InputTokens);
        var row = Assert.Single(session.UsageByModel);
        Assert.Equal(300, row.InputTokens);
        Assert.Equal(30, row.OutputTokens);
        Assert.Equal(2, row.Turns);
    }

    [Fact]
    public void ARaisedBudgetStartsFromTheDefaultCeiling()
    {
        var session = CreateSession();

        var raised = session.RaiseBudget(50_000, 250_000);

        Assert.Equal(300_000, raised);
        Assert.Equal(300_000, session.MaxTotalTokens);
    }

    [Fact]
    public void ARaisedBudgetAddsToTheCeilingTheSessionAlreadyHas()
    {
        var session = CreateSession();
        session.MaxTotalTokens = 1_000;

        Assert.Equal(1_500, session.RaiseBudget(500, 250_000));

        // The step is what the conversation gets, not a replacement for what a resume already recorded.
        Assert.Throws<ArgumentOutOfRangeException>(() => session.RaiseBudget(0, 250_000));
    }

    [Fact]
    public void ARaisedUnlimitedBudgetStaysUnlimited()
    {
        var session = CreateSession();
        session.MaxTotalTokens = long.MaxValue;

        // Adding to an unlimited ceiling must not wrap to a negative number, which would read as a budget that is
        // already spent and end every turn before it starts.
        Assert.Equal(long.MaxValue, session.RaiseBudget(50_000, 250_000));
    }

    [Fact]
    public void EachModelGetsItsOwnRowAndTheTotalIsTheirSum()
    {
        var session = CreateSession();

        session.RecordUsage("deepseek:deepseek-chat", new TokenUsage(10, 1));
        session.RecordUsage("grok:grok-4", new TokenUsage(20, 2));

        Assert.Equal(2, session.UsageByModel.Count);
        Assert.Equal(30, session.TotalUsage.InputTokens);
        Assert.Equal(3, session.TotalUsage.OutputTokens);
    }
}
