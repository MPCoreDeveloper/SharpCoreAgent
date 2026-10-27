using SharpCoreAgent.Application;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Infrastructure.Providers;
using Xunit;

namespace SharpCoreAgent.Tests.Application;

/// <summary>
/// Compaction is the long-session escape hatch, so it must keep the prompt that defines the agent and
/// must never produce a window a provider would reject as a tool result without its call.
/// </summary>
public sealed class AgentSessionCompactionTests
{
    private static AgentSession CreateSession() => new(
        Workspace.Create(Path.GetTempPath()),
        ModelCatalog.DescriptorFor("demo", null),
        ApprovalMode.Ask,
        "system prompt");

    [Fact]
    public void NothingHappensWhenTheTranscriptFits()
    {
        var session = CreateSession();
        session.Add(ChatMessage.User("one"));
        session.Add(ChatMessage.Assistant("two"));

        Assert.Equal(0, session.Compact(8));
        Assert.Equal(3, session.Messages.Count);
    }

    [Fact]
    public void KeepsSystemPromptsAndTheRecentWindow()
    {
        var session = CreateSession();
        session.Add(ChatMessage.User("one"));
        session.Add(ChatMessage.Assistant("two"));
        session.Add(ChatMessage.User("three"));
        session.Add(ChatMessage.Assistant("four"));

        var removed = session.Compact(2);

        Assert.Equal(2, removed);
        Assert.Equal(ChatRole.System, session.Messages[0].Role);
        Assert.Contains("elided", session.Messages[1].Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("three", session.Messages[2].Content);
        Assert.Equal("four", session.Messages[3].Content);
    }

    [Fact]
    public void RefusesToLeaveAToolResultWithoutItsCall()
    {
        var session = CreateSession();
        session.Add(ChatMessage.User("one"));
        session.Add(ChatMessage.Assistant(string.Empty, [new ToolCall("call_1", "read_file", """{"path":"a"}""")]));
        session.Add(ChatMessage.FromToolResult(
            SharpCoreAgent.Domain.Tools.ToolResult.Success("call_1", "read_file", "content")));

        // The most recent message is a tool result, so every cut point would orphan it: nothing is dropped.
        Assert.Equal(0, session.Compact(1));
        Assert.Equal(4, session.Messages.Count);
    }

    [Fact]
    public void RejectsANonPositiveWindow()
    {
        var session = CreateSession();

        Assert.Throws<ArgumentOutOfRangeException>(() => session.Compact(0));
    }

    [Fact]
    public void ModeDefaultsToActAndCanBeSwitched()
    {
        var session = CreateSession();

        Assert.Equal(AgentMode.Act, session.Mode);
        session.Mode = AgentMode.Plan;
        Assert.Equal(AgentMode.Plan, session.Mode);
    }
}
