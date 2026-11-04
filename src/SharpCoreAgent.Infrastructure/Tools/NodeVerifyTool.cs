using SharpCoreAgent.Application;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Runs Node's built-in <c>node --test</c> runner and reports whether the suite passed.
/// </summary>
/// <remarks>
/// The standard test runner keeps the tool dependency-free. A workspace with its own test command can still use
/// <c>run_shell</c>, but the verification gate only accepts this tool for JavaScript profiles.
/// </remarks>
public sealed class NodeVerifyTool : ProcessVerifyTool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = ToolNames.NodeVerify;

    /// <summary>Gets the stable identifier of the Node verification profile.</summary>
    public const string ProfileName = "node";

    /// <summary>Gets the extensions the JavaScript profile covers.</summary>
    public static readonly IReadOnlyList<string> SourceExtensions = [".js", ".mjs", ".cjs"];

    /// <summary>Gets the profile this tool declares to the turn's verification gate.</summary>
    public static readonly IReadOnlyList<VerificationProfile> VerificationProfiles =
        [new(ProfileName, "JavaScript", SourceExtensions)];

    /// <summary>Gets the executable the tool starts by default.</summary>
    public const string DefaultFileName = "node";

    private const string Schema =
        """{"type":"object","properties":{"target":{"type":"string","description":"Test file or directory relative to the workspace root. Omit to let node --test discover from the root."},"timeout_seconds":{"type":"integer","minimum":1,"maximum":900}},"additionalProperties":false}""";

    /// <summary>Initializes a new instance with Node on <c>PATH</c>.</summary>
    public NodeVerifyTool()
        : this(DefaultFileName, new ShellProcessRegistry())
    {
    }

    /// <summary>Initializes a new instance with a shared process registry.</summary>
    /// <param name="processes">The registry that owns the test runs this tool starts.</param>
    public NodeVerifyTool(ShellProcessRegistry processes)
        : this(DefaultFileName, processes)
    {
    }

    /// <summary>Initializes a new instance with an explicit executable.</summary>
    /// <param name="fileName">The Node executable.</param>
    /// <param name="processes">The registry that owns the test runs this tool starts.</param>
    public NodeVerifyTool(string fileName, ShellProcessRegistry processes)
        : base(
            new ToolDefinition(
                Name,
                "Run Node's built-in test runner and report the verdict: exit code, passed or failed, duration and the runner's own output. Use it after changing JavaScript source, because a turn that edited JavaScript is not complete until this has passed. Omit target to let node --test discover from the workspace root.",
                Schema),
            VerificationProfiles,
            fileName,
            "Is Node installed and on PATH?",
            processes)
    {
    }

    /// <inheritdoc />
    protected override string Kind => "node --test";

    /// <inheritdoc />
    protected override IReadOnlyList<string> ArgumentsFor(string? target) =>
        target is null ? ["--test"] : ["--test", target];
}
