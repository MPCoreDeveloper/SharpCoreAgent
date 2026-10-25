using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// <c>run_shell</c> is the one tool that can do anything, so the tests pin the parts the loop depends
/// on: the exit code is reported rather than thrown, a hung command is killed, the working directory is
/// the workspace and credentials stay out of the child environment.
/// </summary>
public sealed class RunShellCommandToolTests
{
    [Fact]
    public async Task ReportsExitCodeAndOutput()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var result = await new RunShellCommandTool().ExecuteAsync(
            ToolTestWorkspace.Call("run_shell", """{"command":"echo hello"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("$ echo hello\nexit code: 0\nhello", result.Content);
        Assert.Equal(new ToolEffect(ToolEffectKind.CommandRun, "echo hello"), Assert.Single(result.Effects));
    }

    [Fact]
    public async Task NonZeroExitCodeIsAResultNotAFailure()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var result = await new RunShellCommandTool().ExecuteAsync(
            ToolTestWorkspace.Call("run_shell", """{"command":"exit 3"}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Contains("exit code: 3", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunsInTheWorkspaceByDefaultAndInTheRequestedSubdirectory()
    {
        using var sandbox = ToolTestWorkspace.Create();
        Directory.CreateDirectory(sandbox.FullPath("src/deep"));
        var printDirectory = OperatingSystem.IsWindows() ? "cd" : "pwd";

        var root = await new RunShellCommandTool().ExecuteAsync(
            ToolTestWorkspace.Call("run_shell", ToolTestWorkspace.Args(new { command = printDirectory })),
            sandbox.Context,
            CancellationToken.None);
        var nested = await new RunShellCommandTool().ExecuteAsync(
            ToolTestWorkspace.Call(
                "run_shell",
                ToolTestWorkspace.Args(new { command = printDirectory, working_directory = "src/deep" })),
            sandbox.Context,
            CancellationToken.None);

        Assert.Contains(sandbox.Root, root.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            Path.Combine("src", "deep"),
            nested.Content,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KillsACommandThatExceedsItsTimeout()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var hang = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "sleep 30";

        var result = await new RunShellCommandTool().ExecuteAsync(
            ToolTestWorkspace.Call(
                "run_shell",
                ToolTestWorkspace.Args(new { command = hang, timeout_seconds = 1 })),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Contains("timed out after 1s", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoesNotHandProviderCredentialsToTheCommand()
    {
        using var sandbox = ToolTestWorkspace.Create();
        const string Name = "SHARPCOREAGENT_TEST_API_KEY";
        const string Value = "sk-should-never-be-visible";
        var command = OperatingSystem.IsWindows() ? $"set {Name}" : $"printenv {Name}";
        var previous = Environment.GetEnvironmentVariable(Name);

        Environment.SetEnvironmentVariable(Name, Value);
        try
        {
            var result = await new RunShellCommandTool().ExecuteAsync(
                ToolTestWorkspace.Call("run_shell", ToolTestWorkspace.Args(new { command })),
                sandbox.Context,
                CancellationToken.None);

            Assert.DoesNotContain(Value, result.Content, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, previous);
        }
    }

    [Fact]
    public async Task RefusesWorkingDirectoryOutsideTheWorkspace()
    {
        using var sandbox = ToolTestWorkspace.Create();

        var result = await new RunShellCommandTool().ExecuteAsync(
            ToolTestWorkspace.Call(
                "run_shell",
                """{"command":"echo hi","working_directory":".."}"""),
            sandbox.Context,
            CancellationToken.None);

        Assert.True(result.IsRefused);
    }
}
