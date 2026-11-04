using SharpCoreAgent.Infrastructure.Tools;
using SharpCoreAgent.Tests.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// The Python and Node verifiers share one process implementation, so the contract tests cover the profile each tool
/// declares, the command it records, and the two refusals that must happen before a runtime is started.
/// </summary>
public sealed class RuntimeVerifyToolTests
{
    [Fact]
    public void PythonDeclaresItsProfileAndCommand()
    {
        var tool = new PythonVerifyTool();

        var profile = Assert.Single(tool.Profiles);
        Assert.Equal(PythonVerifyTool.ProfileName, profile.Name);
        Assert.Equal("Python", profile.DisplayName);
        Assert.True(profile.Matches("src/cart.py"));
        Assert.True(profile.Matches("src/CART.PY"));
        Assert.Equal("python -m unittest discover -v -s tests", tool.CommandFor("tests"));
        Assert.Equal(
            "python -m unittest discover -v -s test -p test_cart.py",
            tool.CommandFor("test/test_cart.py"));
    }

    [Fact]
    public void NodeDeclaresItsProfileAndCommand()
    {
        var tool = new NodeVerifyTool();

        var profile = Assert.Single(tool.Profiles);
        Assert.Equal(NodeVerifyTool.ProfileName, profile.Name);
        Assert.Equal("JavaScript", profile.DisplayName);
        Assert.True(profile.Matches("src/discount.js"));
        Assert.True(profile.Matches("src/DISCOUNT.MJS"));
        Assert.Equal("node --test", tool.CommandFor(null));
        Assert.Equal("node --test test/discount.test.js", tool.CommandFor("test/discount.test.js"));
    }

    [Fact]
    public async Task ATargetOutsideTheWorkspaceIsRefused()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var tool = new PythonVerifyTool();

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(PythonVerifyTool.Name, """{"target":"../outside"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsRefused);
    }

    [Fact]
    public async Task AMissingTargetIsAFailureBeforeTheRuntimeStarts()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var tool = new NodeVerifyTool();

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(NodeVerifyTool.Name, """{"target":"tests/nowhere.js"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.False(result.IsRefused);
        Assert.Contains("tests/nowhere.js", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingRuntimeIsAnErrorThatNamesTheExecutable()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var tool = new PythonVerifyTool("sharpcoreagent-missing-python-runtime", new ShellProcessRegistry());

        var result = await tool.ExecuteAsync(
            ToolTestWorkspace.Call(PythonVerifyTool.Name, """{"target":"."}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.False(result.IsRefused);
        Assert.Contains("sharpcoreagent-missing-python-runtime", result.Content, StringComparison.Ordinal);
        Assert.Contains("Is Python installed", result.Content, StringComparison.Ordinal);
    }
}
