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
    public async Task ACommandThatAsksForInputGetsEndOfInputInsteadOfHanging()
    {
        using var sandbox = ToolTestWorkspace.Create();

        // Both commands read one line from standard input. Given a keyboard — or an open empty pipe —
        // they wait for that line, which is how a tool call ends up swallowing the question typed at the
        // next prompt. Given a closed stdin they see end-of-input at once and the call returns.
        var read = OperatingSystem.IsWindows() ? "set /p line=" : "read line";

        var result = await new RunShellCommandTool().ExecuteAsync(
            ToolTestWorkspace.Call(
                "run_shell",
                ToolTestWorkspace.Args(new { command = read, timeout_seconds = 5 })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        // A refused end-of-input is still a result: what must not happen is the timeout, which arrives
        // as a failure naming the seconds the call waited.
        Assert.False(result.IsError, result.Content);
        Assert.Contains("exit code:", result.Content, StringComparison.Ordinal);
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

    [Fact]
    public async Task OutputIsStreamedWhileTheCommandRuns()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var sink = new CollectingSink();
        var context = sandbox.Context with { Output = sink };

        var result = await new RunShellCommandTool().ExecuteAsync(
            ToolTestWorkspace.Call("run_shell", """{"command":"echo first && echo second"}"""),
            context,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsError);
        Assert.Contains("first", sink.Text, StringComparison.Ordinal);
        Assert.Contains("second", sink.Text, StringComparison.Ordinal);

        // The live view is a courtesy: the result still carries the whole output for the model.
        Assert.Contains("first", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALongCommandHandsBackAHandleThatShellOutputAndShellStopUse()
    {
        using var sandbox = ToolTestWorkspace.Create();
        using var registry = new ShellProcessRegistry();
        var hang = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "sleep 30";

        var started = await new RunShellCommandTool(registry).ExecuteAsync(
            ToolTestWorkspace.Call(
                "run_shell",
                ToolTestWorkspace.Args(new { command = hang, wait_seconds = 1 })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.False(started.IsError);
        Assert.Contains("running: sh-1", started.Content, StringComparison.Ordinal);

        var read = await new ShellOutputTool(registry).ExecuteAsync(
            ToolTestWorkspace.Call(
                ShellOutputTool.Name,
                ToolTestWorkspace.Args(new { process_id = "sh-1", wait_seconds = 1 })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.False(read.IsError);
        Assert.StartsWith("sh-1 (", read.Content, StringComparison.Ordinal);

        var stopped = await new ShellStopTool(registry).ExecuteAsync(
            ToolTestWorkspace.Call(ShellStopTool.Name, """{"process_id":"sh-1"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.False(stopped.IsError);
        Assert.Contains("stopped sh-1", stopped.Content, StringComparison.Ordinal);
        Assert.Empty(registry.Running);
    }

    [Fact]
    public async Task AHandleNobodyIssuedIsRejected()
    {
        using var sandbox = ToolTestWorkspace.Create();
        using var registry = new ShellProcessRegistry();

        var read = await new ShellOutputTool(registry).ExecuteAsync(
            ToolTestWorkspace.Call(ShellOutputTool.Name, """{"process_id":"sh-404"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);
        var stop = await new ShellStopTool(registry).ExecuteAsync(
            ToolTestWorkspace.Call(ShellStopTool.Name, """{"process_id":"sh-404"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(read.IsError);
        Assert.Contains("no process with id 'sh-404'", read.Content, StringComparison.Ordinal);
        Assert.True(stop.IsError);
        Assert.Contains("no process with id 'sh-404'", stop.Content, StringComparison.Ordinal);
    }


    [Fact]
    public async Task AFinishedBackgroundCommandReportsItsExitCodeAndStopIsIdempotent()
    {
        using var sandbox = ToolTestWorkspace.Create();
        using var registry = new ShellProcessRegistry();

        var started = await new RunShellCommandTool(registry).ExecuteAsync(
            ToolTestWorkspace.Call(
                "run_shell",
                ToolTestWorkspace.Args(new { command = "exit 4", wait_seconds = 0 })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.False(started.IsError);
        Assert.Contains("running: sh-1", started.Content, StringComparison.Ordinal);

        // Waiting for the exit is what shell_output is for, and it is what makes the code observable.
        var read = await new ShellOutputTool(registry).ExecuteAsync(
            ToolTestWorkspace.Call(
                ShellOutputTool.Name,
                ToolTestWorkspace.Args(new { process_id = "sh-1", wait_seconds = 10 })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.False(read.IsError);
        Assert.Contains("exited 4", read.Content, StringComparison.Ordinal);

        var stopped = await new ShellStopTool(registry).ExecuteAsync(
            ToolTestWorkspace.Call(ShellStopTool.Name, """{"process_id":"sh-1"}"""),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.False(stopped.IsError);
        Assert.Contains("had already exited with code 4", stopped.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposingTheRegistryForgetsEveryHandle()
    {
        using var sandbox = ToolTestWorkspace.Create();
        var registry = new ShellProcessRegistry();

        await new RunShellCommandTool(registry).ExecuteAsync(
            ToolTestWorkspace.Call(
                "run_shell",
                ToolTestWorkspace.Args(new { command = "exit 0", wait_seconds = 0 })),
            sandbox.Context,
            TestContext.Current.CancellationToken);

        Assert.True(registry.TryGet("sh-1", out var tracked));
        registry.Dispose();

        Assert.False(registry.TryGet("sh-1", out _));
        Assert.Empty(registry.Running);
        Assert.True(tracked.HasExited);
    }

    /// <summary>Collects the live output a tool forwards, so a test can assert on it.</summary>
    private sealed class CollectingSink : IToolOutputSink
    {
        private readonly System.Text.StringBuilder _text = new();
        private readonly object _gate = new();

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return _text.ToString();
                }
            }
        }

        public ValueTask WriteAsync(string text, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _text.Append(text);
            }

            return ValueTask.CompletedTask;
        }
    }
}

