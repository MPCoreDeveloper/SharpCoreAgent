using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SharpCoreAgent.Application;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Builds or tests the .NET workspace and reports what the compiler or the test runner said.
/// </summary>
/// <remarks>
/// This is the tool the turn loop gates a completion on: a turn that edits .NET source may not end on the
/// model's word, and this is the question it has to answer instead. The command runs through
/// <see cref="ShellProcessRegistry"/> — the same host as <c>run_shell</c> — so the process is tracked, killed on
/// timeout and cleaned up when the host exits, and this tool adds no second door to the machine
/// (Principle III).
/// <para>
/// A build that fails is an error result, which is the opposite of what <c>run_shell</c> does with the same
/// exit code. The two are not inconsistent: a shell command reports that it ran, and a verifier reports
/// whether it passed. The diagnostics travel in the text either way, so nothing is lost by saying so.
/// </para>
/// <para>
/// The child is told to speak English because <see cref="DotnetVerifyReportParser"/> reads English. The
/// developer's own locale is untouched: it applies to the commands they run through <c>run_shell</c>, not to the
/// one the agent runs to check its work.
/// </para>
/// </remarks>
public sealed class DotnetVerifyTool : ITool, IVerificationTool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = ToolNames.DotnetVerify;

    /// <summary>Gets the stable identifier of the .NET verification profile.</summary>
    public const string ProfileName = "dotnet";

    /// <summary>Gets the extensions whose creation, modification or deletion can break a .NET build.</summary>
    /// <remarks>
    /// A constant rather than a language lookup: deciding whether a file is part of a .NET build is the build's job, and
    /// the compiler is the one that answers it. A file that turns out not to be compiled costs a nudge and a build that
    /// passes in a second, which is cheaper than a rule that guesses.
    /// </remarks>
    public static readonly IReadOnlyList<string> SourceExtensions =
        [".cs", ".vb", ".fs", ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".sln", ".slnx"];

    /// <summary>Gets the profile this tool declares to the turn's verification gate.</summary>
    public static readonly IReadOnlyList<VerificationProfile> VerificationProfiles =
        [new(ProfileName, ".NET", SourceExtensions)];

    /// <summary>Gets the kind that compiles the workspace.</summary>
    public const string Build = "build";

    /// <summary>Gets the kind that compiles and runs the tests.</summary>
    public const string Test = "test";

    /// <summary>Gets how long a run may take before it is killed, in seconds.</summary>
    public const int DefaultTimeoutSeconds = 300;

    /// <summary>Gets the longest timeout a caller may request, in seconds.</summary>
    public const int MaxTimeoutSeconds = 900;

    /// <summary>Gets the executable the tool starts by default.</summary>
    public const string DefaultFileName = "dotnet";

    private const string Schema =
        """{"type":"object","properties":{"kind":{"type":"string","enum":["build","test"],"description":"build compiles the workspace and reports diagnostics; test also runs the test suite."},"project":{"type":"string","description":"Project or solution to narrow the run to, relative to the workspace root. Omit to build whatever the root contains."},"filter":{"type":"string","description":"Test-name filter (test only); passed as --filter. A value that matches no test is a failure."},"no_build":{"type":"boolean","description":"Test only: run against already-built binaries with --no-build. It does not compile, so it does not close the compile gate."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":900}},"required":["kind"],"additionalProperties":false}""";

    /// <summary>Gets how long the call waits for the output readers after the process exits.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromMilliseconds(250);

    private readonly string _dotnetFileName;
    private readonly ShellProcessRegistry _processes;

    /// <summary>
    /// Initializes a new instance of the <see cref="DotnetVerifyTool"/> class with the SDK on <c>PATH</c>.
    /// </summary>
    public DotnetVerifyTool()
        : this(DefaultFileName, new ShellProcessRegistry())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotnetVerifyTool"/> class with a shared process registry.
    /// </summary>
    /// <param name="processes">The registry that owns the compiler runs this tool starts.</param>
    public DotnetVerifyTool(ShellProcessRegistry processes)
        : this(DefaultFileName, processes)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DotnetVerifyTool"/> class with an explicit executable.
    /// </summary>
    /// <param name="dotnetFileName">The SDK executable; the container passes <c>dotnet</c>.</param>
    /// <param name="processes">The registry that owns the compiler runs this tool starts.</param>
    /// <exception cref="ArgumentException"><paramref name="dotnetFileName"/> is blank.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="processes"/> is null.</exception>
    public DotnetVerifyTool(string dotnetFileName, ShellProcessRegistry processes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetFileName);
        ArgumentNullException.ThrowIfNull(processes);

        _dotnetFileName = dotnetFileName;
        _processes = processes;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Build or test the .NET workspace and report the verdict: exit code, passed or failed, warnings, diagnostics, test counts and the compiler's own log. Use it after changing .NET source, because a turn that edited code is not complete until this has passed. A failing build is returned as an error that names every diagnostic.",
        Schema);

    /// <inheritdoc />
    public ToolSafety Safety => ToolSafety.Destructive;

    /// <inheritdoc />
    public IReadOnlyList<VerificationProfile> Profiles => VerificationProfiles;

    /// <summary>
    /// Builds the command line a run is described by.
    /// </summary>
    /// <param name="kind">The kind of run: <see cref="Build"/> or <see cref="Test"/>.</param>
    /// <param name="project">The workspace-relative project or solution, or null for the root.</param>
    /// <returns>The command as it appears in the result header and in the turn's effect list.</returns>
    public static string CommandFor(string kind, string? project) =>
        string.IsNullOrEmpty(project)
            ? string.Concat(DefaultFileName, " ", kind)
            : string.Concat(DefaultFileName, " ", kind, " ", project);

    /// <inheritdoc />
    public async ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        var kind = arguments.GetRequiredString("kind").Trim().ToLowerInvariant();
        if (kind is not (Build or Test))
        {
            return ToolResult.Failure(toolCall.Id, Name, $"kind must be '{Build}' or '{Test}', not '{kind}'.");
        }

        var filter = arguments.GetOptionalString("filter");
        var noBuild = arguments.GetOptionalBool("no_build", false);
        if (kind == Build && (filter is { Length: > 0 } || noBuild))
        {
            return ToolResult.Failure(toolCall.Id, Name, "'filter' and 'no_build' apply only to kind 'test'.");
        }

        var timeoutSeconds = arguments.GetOptionalInt(
            "timeout_seconds",
            DefaultTimeoutSeconds,
            minimum: 1,
            maximum: MaxTimeoutSeconds);
        var projectArgument = arguments.GetOptionalString("project");

        var workingDirectory = context.Workspace.Root;
        string? project = null;
        if (projectArgument is { Length: > 0 })
        {
            if (!PathSafety.TryResolveWrite(
                context.Workspace,
                context.Approval,
                projectArgument,
                out var resolved,
                out _,
                out var error))
            {
                return ToolResult.Refused(toolCall.Id, Name, error!);
            }

            if (!File.Exists(resolved) && !Directory.Exists(resolved))
            {
                return ToolResult.Failure(
                    toolCall.Id,
                    Name,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"no project or solution at '{context.Workspace.ToRelative(resolved)}'; omit project to build whatever the workspace root contains."));
            }

            project = context.Workspace.ToRelative(resolved);
        }

        var command = CommandFor(kind, project);
        if (filter is { Length: > 0 })
        {
            command = string.Concat(command, " --filter ", filter);
        }

        if (noBuild)
        {
            command = string.Concat(command, " --no-build");
        }

        ShellProcess process;
        try
        {
            process = _processes.Start(
                command,
                CreateStartInfo(kind, project, workingDirectory, filter, noBuild),
                context.Output,
                TimeSpan.FromSeconds(timeoutSeconds));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"could not start '{_dotnetFileName}': {ex.Message}. Is the .NET SDK installed and on PATH?");
        }

        var started = Stopwatch.GetTimestamp();
        var finished = false;
        try
        {
            finished = await process
                .WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The turn was cancelled: kill the tree before letting the cancellation through, or the compiler
            // would keep writing obj/ after the developer pressed Ctrl+C.
            process.TryStop();
            throw;
        }

        if (!finished)
        {
            process.TryStop();
            var (partial, _) = await DrainAsync(process).ConfigureAwait(false);
            var message = string.Create(
                CultureInfo.InvariantCulture,
                $"'{command}' timed out after {timeoutSeconds}s and was killed. Raise timeout_seconds, or run the build with run_shell and poll it with shell_output.");

            return ToolResult.Failure(
                toolCall.Id,
                Name,
                partial.Length == 0 ? message : string.Concat(message, "\n", partial));
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        var (output, truncated) = await DrainAsync(process).ConfigureAwait(false);
        var report = DotnetVerifyReportParser.Parse(process.ExitCode ?? -1, output);
        var content = Render(kind, project ?? "(workspace root)", report, elapsed, output, truncated);

        var result = new ToolResult
        {
            CallId = toolCall.Id,
            ToolName = Name,
            Content = content,
            IsError = !report.Succeeded,

            // Recorded even when the build failed: the turn's own report should say a compiler ran, and
            // "dotnet build" in `CommandsRun` beside a failed result is the honest description of the turn.
            Effects = [new ToolEffect(ToolEffectKind.CommandRun, command)],

            // A run that skipped compilation is not evidence the source builds, so it must not close the compile gate.
            CountsAsVerification = !noBuild,
        };

        return result with { IsTruncated = truncated };
    }

    /// <summary>
    /// Renders the labelled header, the diagnostics and the log the model reads.
    /// </summary>
    /// <param name="kind">The kind of run.</param>
    /// <param name="project">The project as it is named in the result.</param>
    /// <param name="report">What the parser found.</param>
    /// <param name="elapsed">How long the run took.</param>
    /// <param name="output">The bounded log.</param>
    /// <param name="truncated">Whether the log was shortened.</param>
    /// <returns>The result text.</returns>
    private static string Render(
        string kind,
        string project,
        DotnetVerifyReport report,
        TimeSpan elapsed,
        string output,
        bool truncated)
    {
        var builder = new StringBuilder();
        builder.Append("kind         ").AppendLine(kind);
        builder.Append("project      ").AppendLine(project);
        builder.Append("exit code    ").AppendLine(report.ExitCode.ToString(CultureInfo.InvariantCulture));
        builder.Append("result       ").AppendLine(report.Succeeded ? "passed" : "failed");
        builder.Append("warnings     ").AppendLine(report.WarningCount.ToString(CultureInfo.InvariantCulture));
        builder.Append("duration     ").AppendLine(string.Create(CultureInfo.InvariantCulture, $"{elapsed.TotalSeconds:0.0}s"));
        builder.Append("summary      ").AppendLine(report.Summary);

        if (report.TestsPassed is { } passed)
        {
            builder.Append("tests        ")
                .Append(passed.ToString(CultureInfo.InvariantCulture))
                .Append(" passed, ")
                .Append((report.TestsFailed ?? 0).ToString(CultureInfo.InvariantCulture))
                .AppendLine(" failed");
        }

        if (truncated)
        {
            builder.AppendLine("truncated    yes");
        }

        if (report.Errors.Count == 0)
        {
            builder.AppendLine("errors       none");
        }
        else
        {
            builder.Append("errors       ").AppendLine(report.Errors.Count.ToString(CultureInfo.InvariantCulture));
            for (var index = 0; index < report.Errors.Count; index++)
            {
                builder.Append("  ")
                    .Append((index + 1).ToString(CultureInfo.InvariantCulture))
                    .Append(". ")
                    .AppendLine(report.Errors[index]);
            }
        }

        builder.AppendLine();
        builder.AppendLine(output.Length == 0 ? "(no output)" : output);
        return builder.ToString();
    }

    /// <summary>
    /// Collects what the run printed, bounded and without its final newline.
    /// </summary>
    /// <param name="process">The process to drain.</param>
    /// <returns>The text, and whether it was shortened to fit the budget.</returns>
    private static async Task<(string Text, bool Truncated)> DrainAsync(ShellProcess process)
    {
        var text = await process.DrainAsync(DrainGrace).ConfigureAwait(false);
        var bounded = TextLimits.Bound(text.TrimEnd('\n', '\r'), TextLimits.MaxCommandOutputChars, out var truncated);
        return (bounded, truncated);
    }

    /// <summary>
    /// Builds how the SDK is started: one verb, an optional path, no shell.
    /// </summary>
    /// <param name="kind">The kind of run.</param>
    /// <param name="project">The workspace-relative project, or null for the root.</param>
    /// <param name="workingDirectory">The directory the run starts in.</param>
    /// <param name="filter">The test-name filter, or null; passed as <c>--filter</c> for a test run.</param>
    /// <param name="noBuild">Whether to run against already-built binaries with <c>--no-build</c>.</param>
    /// <returns>The start information.</returns>
    /// <remarks>
    /// The arguments travel as a list rather than as a command line, so a workspace path with a space needs no
    /// quoting and there is no string for a caller to break out of — the same reading of Principle III that
    /// <c>run_shell</c> applies to the command it hands its shell.
    /// </remarks>
    private ProcessStartInfo CreateStartInfo(string kind, string? project, string workingDirectory, string? filter, bool noBuild)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _dotnetFileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // A compiler is not a conversation partner: it gets end-of-input immediately, so it can never
            // swallow the line the developer is about to type at the CLI's own prompt.
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = TextLimits.Utf8NoBom,
            StandardErrorEncoding = TextLimits.Utf8NoBom,
        };

        startInfo.ArgumentList.Add(kind);
        if (!string.IsNullOrEmpty(project))
        {
            startInfo.ArgumentList.Add(project);
        }

        if (filter is { Length: > 0 })
        {
            startInfo.ArgumentList.Add("--filter");
            startInfo.ArgumentList.Add(filter);
        }

        if (noBuild)
        {
            startInfo.ArgumentList.Add("--no-build");
        }

        // The parser reads English markers ("Build FAILED.", "N Warning(s)"); a localized SDK would answer
        // "exit code 1" with no summary at all. This is the child's locale, not the developer's.
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        ChildEnvironment.RemoveSecrets(startInfo);

        return startInfo;
    }
}

