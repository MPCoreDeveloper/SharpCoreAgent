using System.Diagnostics;
using System.Globalization;
using SharpCoreAgent.Domain;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>Runs one read-only Git observation through the shell process host.</summary>
internal sealed class GitCommand(ShellProcessRegistry processes, string fileName = "git")
{
    public const string DefaultFileName = "git";
    public const int DefaultTimeoutSeconds = 30;
    private static readonly TimeSpan DrainGrace = TimeSpan.FromMilliseconds(250);
    private static readonly string[] RedirectingVariables =
    [
        "GIT_DIR",
        "GIT_WORK_TREE",
        "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES",
        "GIT_COMMON_DIR",
        "GIT_NAMESPACE",
        "GIT_GRAFT_FILE",
        "GIT_EXTERNAL_DIFF",
        "GIT_DIFF_OPTS",
        "GIT_CONFIG_PARAMETERS",
        "GIT_CONFIG_COUNT",
    ];

    public async ValueTask<GitOutcome> RunAsync(
        Workspace workspace,
        IReadOnlyList<string> command,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "--no-optional-locks",
            "--no-pager",
            "-c", "core.quotepath=false",
            "-c", "core.fsmonitor=false",
            "-c", "status.relativePaths=true",
            "-c", "diff.noprefix=false",
            "-c", "diff.mnemonicPrefix=false",
        };
        arguments.AddRange(command);

        ShellProcess process;
        try
        {
            process = processes.Start(
                Display(arguments),
                StartInfo(workspace.Root, arguments),
                null,
                timeout);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"could not start '{fileName}': {ex.Message}. Is Git installed and on PATH?",
                ex);
        }

        using (process)
        {
            if (!await process.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
            {
                process.TryStop();
                await process.DrainAsync(DrainGrace).ConfigureAwait(false);
                return new GitOutcome(ExitCode: -1, Output: string.Empty, TimedOut: true);
            }

            var output = await process.DrainAsync(DrainGrace).ConfigureAwait(false);
            return new GitOutcome(process.ExitCode ?? -1, output.TrimEnd('\n', '\r'), TimedOut: false);
        }
    }

    /// <summary>Converts an already-confined absolute path to the pathspec Git expects.</summary>
    /// <param name="workspace">The workspace the path belongs to.</param>
    /// <param name="fullPath">The path returned by <see cref="PathSafety"/>.</param>
    /// <returns>A workspace-relative path, with Windows separators folded to Git's slash.</returns>
    public static string ToPathspec(Workspace workspace, string fullPath)
    {
        var relative = workspace.ToRelative(fullPath);
        return Path.DirectorySeparatorChar == '\\' ? relative.Replace('\\', '/') : relative;
    }

    private ProcessStartInfo StartInfo(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
#pragma warning disable S4036 // Git is resolved through PATH by design; the argv after it is fixed and model input is only a pathspec after '--'.
            FileName = fileName,
#pragma warning restore S4036
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = TextLimits.Utf8NoBom,
            StandardErrorEncoding = TextLimits.Utf8NoBom,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_PAGER"] = "cat";
        startInfo.Environment["PAGER"] = "cat";
        startInfo.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        startInfo.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        startInfo.Environment["LC_ALL"] = "C";
        RemoveRedirectingVariables(startInfo);
        ChildEnvironment.RemoveSecrets(startInfo);
        return startInfo;
    }

    /// <summary>
    /// Removes environment variables that would point Git at another repository or inject configuration.
    /// </summary>
    /// <param name="startInfo">The child environment to filter.</param>
    /// <remarks>
    /// A developer's shell may export <c>GIT_DIR</c> for their own workflow. Inheriting it would make the workspace
    /// boundary a suggestion rather than the thing the path check enforced. Command-line configuration still comes last,
    /// and literal pathspecs prevent a name after <c>--</c> from becoming Git pathspec magic.
    /// </remarks>
    private static void RemoveRedirectingVariables(ProcessStartInfo startInfo)
    {
        foreach (var name in startInfo.Environment.Keys
            .Where(IsRedirectingVariable)
            .ToArray())
        {
            startInfo.Environment.Remove(name);
        }
    }

    private static bool IsRedirectingVariable(string name) =>
        RedirectingVariables.Contains(name, StringComparer.OrdinalIgnoreCase) ||
        name.StartsWith("GIT_CONFIG_KEY_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase);

    private string Display(IEnumerable<string> arguments) =>
        string.Create(CultureInfo.InvariantCulture, $"{fileName} {string.Join(' ', arguments)}");
}

/// <summary>The exit code and captured text of one Git observation.</summary>
internal sealed record GitOutcome(int ExitCode, string Output, bool TimedOut);
