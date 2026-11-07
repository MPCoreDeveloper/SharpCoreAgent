using System.Text.Json;
using SharpCoreAgent.Cli;
using SharpCoreAgent.Domain.Agents;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The JSON lines the page reads: the same shapes <c>ConsoleObserver --json</c> prints, asserted by key.
/// </summary>
/// <remarks>
/// The page switches on <c>type</c> and reads one or two fields from each frame, so a rename here breaks the page silently.
/// These cases pin the contract the script depends on.
/// </remarks>
public sealed class ServeEventsTests
{
    [Fact]
    public void TheTextAndReasoningFramesCarryTheirTypeAndPayload()
    {
        AssertFrame(ServeEvents.Text("hello"), "text_delta", "text", "hello");
        AssertFrame(ServeEvents.Thinking("hmm"), "thinking_delta", "text", "hmm");
        AssertFrame(ServeEvents.Notice("note"), "notice", "message", "note");
        AssertFrame(ServeEvents.Error("bad"), "error", "message", "bad");
    }

    [Fact]
    public void AToolFrameCarriesTheCallTheToolAndItsArguments()
    {
        using var started = JsonDocument.Parse(ServeEvents.ToolStarted("c1", "write_file", "{\"path\":\"a.cs\"}"));
        Assert.Equal("tool_started", started.RootElement.GetProperty("type").GetString());
        Assert.Equal("c1", started.RootElement.GetProperty("callId").GetString());
        Assert.Equal("write_file", started.RootElement.GetProperty("tool").GetString());
        Assert.Equal("a.cs", started.RootElement.GetProperty("arguments").GetProperty("path").GetString());

        using var finished = JsonDocument.Parse(ServeEvents.ToolFinished("c1", "write_file", false, "boom"));
        Assert.False(finished.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("boom", finished.RootElement.GetProperty("summary").GetString());
    }

    [Fact]
    public void MalformedArgumentsAreShownAsTextRatherThanDropped()
    {
        using var started = JsonDocument.Parse(ServeEvents.ToolStarted("c1", "x", "not json"));

        Assert.Equal("not json", started.RootElement.GetProperty("arguments").GetString());
    }

    [Fact]
    public void TheApprovalFrameCarriesThePreviewAndTheId()
    {
        using var frame = JsonDocument.Parse(
            ServeEvents.ApprovalRequested(7, "write_file", "write a.cs", "{}", "@@ -1 +1 @@"));

        Assert.Equal("approval_requested", frame.RootElement.GetProperty("type").GetString());
        Assert.Equal(7, frame.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("@@ -1 +1 @@", frame.RootElement.GetProperty("preview").GetString());
    }

    [Fact]
    public void TheReportFrameSummarizesTheTurn()
    {
        var report = new AgentReport { Outcome = AgentOutcome.Completed, ModelId = "demo:demo", Iterations = 2 };

        using var frame = JsonDocument.Parse(ServeEvents.Report(report));

        Assert.Equal("report", frame.RootElement.GetProperty("type").GetString());
        Assert.Equal("completed", frame.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("demo:demo", frame.RootElement.GetProperty("model").GetString());
        Assert.Equal(2, frame.RootElement.GetProperty("iterations").GetInt32());
    }

    private static void AssertFrame(string json, string type, string key, string value)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(type, document.RootElement.GetProperty("type").GetString());
        Assert.Equal(value, document.RootElement.GetProperty(key).GetString());
    }
}
