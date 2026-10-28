using SharpCoreAgent.Application;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Infrastructure.Providers;
using Xunit;

namespace SharpCoreAgent.Tests.Application;

/// <summary>
/// A per-tool approval is the narrow answer to "stop asking": it silences one tool for one conversation and
/// nothing else, and it has to be revocable because a standing yes outlives the work that earned it.
/// </summary>
public sealed class AgentSessionApprovalTests
{
    private static AgentSession CreateSession() => new(
        Workspace.Create(Path.GetTempPath()),
        ModelCatalog.DescriptorFor("demo", null),
        ApprovalMode.Ask,
        "system prompt");

    [Fact]
    public void NoToolIsApprovedOnItsOwn()
    {
        var session = CreateSession();

        Assert.Empty(session.ApprovedTools);
        Assert.False(session.IsToolApproved("run_shell_command"));
        Assert.Equal(ApprovalMode.Ask, session.Approval);
    }

    [Fact]
    public void OneToolIsApprovedWithoutChangingTheMode()
    {
        var session = CreateSession();

        session.ApproveTool("run_shell_command");

        Assert.True(session.IsToolApproved("run_shell_command"));

        // The mode is untouched on purpose: a file edit still has to ask, which is the whole difference
        // between "this tool" and "every tool".
        Assert.Equal(ApprovalMode.Ask, session.Approval);
        Assert.False(session.IsToolApproved("write_file"));
    }

    [Fact]
    public void TheNameIsMatchedTheWayTheRegistryResolvesIt()
    {
        var session = CreateSession();

        session.ApproveTool("Run_Shell_Command");

        Assert.True(session.IsToolApproved("run_shell_command"));
    }

    [Fact]
    public void ApprovingTwiceIsNotTwoTools()
    {
        var session = CreateSession();

        session.ApproveTool("run_shell_command");
        session.ApproveTool("run_shell_command");

        Assert.Single(session.ApprovedTools);
    }

    [Fact]
    public void ForgettingRevokesWhatWasAllowed()
    {
        var session = CreateSession();
        session.ApproveTool("run_shell_command");
        session.ApproveTool("write_file");

        Assert.Equal(2, session.ForgetApprovedTools());

        Assert.Empty(session.ApprovedTools);
        Assert.False(session.IsToolApproved("run_shell_command"));

        // Revoking an empty session is not an error: /approval forget has to be safe to type twice.
        Assert.Equal(0, session.ForgetApprovedTools());
    }

    [Fact]
    public void AnEmptyNameIsRejectedRatherThanApprovingEverything()
    {
        var session = CreateSession();

        Assert.Throws<ArgumentException>(() => session.ApproveTool(" "));
        Assert.False(session.IsToolApproved(" "));
        Assert.False(session.IsToolApproved(string.Empty));
    }
}
