using System.Diagnostics;
using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Tools;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>A throwaway repository for the read-only Git tools.</summary>
internal sealed class GitSandbox : IDisposable
{
    private readonly ToolTestWorkspace _workspace;

    private GitSandbox(ToolTestWorkspace workspace, string initialContent)
    {
        _workspace = workspace;
        Run("init", "--quiet");
        Run("config", "user.name", "SharpCoreAgent Tests");
        Run("config", "user.email", "tests@example.invalid");
        Run("config", "commit.gpgsign", "false");
        Run("config", "core.autocrlf", "false");
        workspace.Write("tracked.txt", initialContent);
        Commit("initial");
    }

    public string Root => _workspace.Root;

    public ToolInvocationContext Context => _workspace.Context;

    public static GitSandbox Create(string initialContent = "one\ntwo\n")
    {
        return new GitSandbox(ToolTestWorkspace.Create(), initialContent);
    }

    public string Write(string path, string content) => _workspace.Write(path, content);

    public void Add(params string[] paths)
    {
        var arguments = new List<string> { "add", "--" };
        arguments.AddRange(paths);
        Run([.. arguments]);
    }

    public void Commit(string message)
    {
        Run("add", "-A");
        Run("commit", "--quiet", "-m", message);
    }

    public string Run(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var registry = new ShellProcessRegistry();
        var timeout = TimeSpan.FromSeconds(30);
        using var process = registry.Start(
            string.Create(CultureInfo.InvariantCulture, $"git {string.Join(' ', arguments)}"),
            startInfo,
            null,
            timeout);
        if (!process.WaitAsync(timeout, CancellationToken.None).GetAwaiter().GetResult())
        {
            process.TryStop();
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"git {string.Join(' ', arguments)} timed out"));
        }

        var output = process.DrainAsync(TimeSpan.FromMilliseconds(250)).GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"git {string.Join(' ', arguments)} failed with {process.ExitCode}: {output}"));
        }

        return output;
    }

    public void Dispose() => _workspace.Dispose();
}
