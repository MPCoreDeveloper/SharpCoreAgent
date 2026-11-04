using SharpCoreAgent.Application;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Runs Python's built-in <c>unittest</c> discovery and reports whether the suite passed.
/// </summary>
/// <remarks>
/// The tool intentionally uses the standard library rather than assuming pytest is installed. A workspace that needs
/// pytest can still use <c>run_shell</c>, but the verification gate only accepts this tool for Python profiles.
/// </remarks>
public sealed class PythonVerifyTool : ProcessVerifyTool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = ToolNames.PythonVerify;

    /// <summary>Gets the stable identifier of the Python verification profile.</summary>
    public const string ProfileName = "python";

    /// <summary>Gets the extensions the Python profile covers.</summary>
    public static readonly IReadOnlyList<string> SourceExtensions = [".py", ".pyw"];

    /// <summary>Gets the profile this tool declares to the turn's verification gate.</summary>
    public static readonly IReadOnlyList<VerificationProfile> VerificationProfiles =
        [new(ProfileName, "Python", SourceExtensions)];

    /// <summary>Gets the executable the tool starts by default.</summary>
    public static string DefaultFileName => OperatingSystem.IsWindows() ? "python" : "python3";

    private const string Schema =
        """{"type":"object","properties":{"target":{"type":"string","description":"Test file or directory relative to the workspace root. Omit to discover from the root."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":900}},"additionalProperties":false}""";

    /// <summary>Initializes a new instance with Python on <c>PATH</c>.</summary>
    public PythonVerifyTool()
        : this(DefaultFileName, new ShellProcessRegistry())
    {
    }

    /// <summary>Initializes a new instance with a shared process registry.</summary>
    /// <param name="processes">The registry that owns the test runs this tool starts.</param>
    public PythonVerifyTool(ShellProcessRegistry processes)
        : this(DefaultFileName, processes)
    {
    }

    /// <summary>Initializes a new instance with an explicit executable.</summary>
    /// <param name="fileName">The Python executable.</param>
    /// <param name="processes">The registry that owns the test runs this tool starts.</param>
    public PythonVerifyTool(string fileName, ShellProcessRegistry processes)
        : base(
            new ToolDefinition(
                Name,
                "Run Python's unittest discovery and report the verdict: exit code, passed or failed, duration and the test runner's own output. Use it after changing Python source, because a turn that edited Python is not complete until this has passed. Omit target to discover from the workspace root.",
                Schema),
            VerificationProfiles,
            fileName,
            "Is Python installed and on PATH?",
            processes)
    {
    }

    /// <inheritdoc />
    protected override string Kind => "unittest";

    /// <inheritdoc />
    protected override IReadOnlyList<string> ArgumentsFor(string? target)
    {
        if (target is null)
        {
            return ["-m", "unittest", "discover", "-v"];
        }

        // A file target becomes discovery's pattern inside its directory. A directory target is handed to -s
        // directly. Discovery is used for both so a single file does not need a module name or a __main__ block.
        if (Path.HasExtension(target))
        {
            var directory = Path.GetDirectoryName(target)?.Replace('\\', '/');
            return
            [
                "-m", "unittest", "discover", "-v",
                "-s", string.IsNullOrEmpty(directory) ? "." : directory,
                "-p", Path.GetFileName(target),
            ];
        }

        return ["-m", "unittest", "discover", "-v", "-s", target];
    }
}
