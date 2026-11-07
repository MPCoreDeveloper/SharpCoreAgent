using System.Text.Json;
using SharpCoreAgent.Cli;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Cli;

/// <summary>
/// The bridge between the page's buttons and the loop's decision: the words it maps, the answer it completes, and the
/// denial a cancel becomes.
/// </summary>
public sealed class WebApproverTests
{
    [Theory]
    [InlineData("approve", ApprovalOutcome.Approved)]
    [InlineData("yes", ApprovalOutcome.Approved)]
    [InlineData("tool", ApprovalOutcome.ApprovedForTool)]
    [InlineData("t", ApprovalOutcome.ApprovedForTool)]
    [InlineData("all", ApprovalOutcome.ApprovedForSession)]
    [InlineData("always", ApprovalOutcome.ApprovedForSession)]
    [InlineData("deny", ApprovalOutcome.Denied)]
    [InlineData("something", ApprovalOutcome.Denied)]
    [InlineData("", ApprovalOutcome.Denied)]
    [InlineData(null, ApprovalOutcome.Denied)]
    public void ThePagesWordMapsToTheLoopsOutcome(string? word, ApprovalOutcome expected) =>
        Assert.Equal(expected, WebApprover.Parse(word));

    [Fact]
    public async Task AnAnswerCompletesTheWaitingQuestion()
    {
        var hub = new ServeHub();
        using var lease = hub.Subscribe(out var reader);
        var approver = new WebApprover(hub);

        var pending = approver.DecideAsync(Request(), CancellationToken.None).AsTask();

        var frame = await reader.ReadAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(frame);
        Assert.Equal("approval_requested", document.RootElement.GetProperty("type").GetString());
        var id = document.RootElement.GetProperty("id").GetInt64();

        Assert.True(approver.Resolve(id, "approve"));
        Assert.Equal(ApprovalOutcome.Approved, await pending);

        // A second answer for the same id has nothing left to complete.
        Assert.False(approver.Resolve(id, "deny"));
    }

    [Fact]
    public async Task CancellingWhileAQuestionIsOpenDenies()
    {
        var hub = new ServeHub();
        var approver = new WebApprover(hub);
        using var cancellation = new CancellationTokenSource();

        var pending = approver.DecideAsync(Request(), cancellation.Token).AsTask();
        await cancellation.CancelAsync();

        Assert.Equal(ApprovalOutcome.Denied, await pending);
    }

    private static ToolApprovalRequest Request() =>
        new("write_file", ToolSafety.Destructive, "write a.cs", "{\"path\":\"a.cs\"}");
}
