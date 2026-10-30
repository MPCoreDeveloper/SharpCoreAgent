using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Code;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Code;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Asks the language service what the compiler says about one file, without building anything.
/// </summary>
/// <remarks>
/// This is the fast half of the verification loop. <c>dotnet_verify</c> is the authority — it is what marks a turn
/// verified — but it costs a build, and a model iterating on one file pays for that build on every attempt.
/// <see cref="Name"/> answers from the compiler's front end in-process, so the loop is "edit, check, edit" and the
/// build is run once at the end.
/// <para>
/// The tool is thin on purpose: it validates two arguments, forwards one call and prints what came back. Which
/// language answered, and whether it needed a project or a restore, is not its business — that is the router's, and
/// the failure codes it passes through are the adapter's.
/// </para>
/// </remarks>
public sealed class FileDiagnosticsTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "file_diagnostics";

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"Workspace-relative path to a source file, for example src/Program.fs."}},"required":["path"],"additionalProperties":false}""";

    private readonly ICodeDiagnostics _diagnostics;

    /// <summary>
    /// Initialises the tool.
    /// </summary>
    /// <param name="diagnostics">The code-intelligence surface this host registered.</param>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostics"/> is null.</exception>
    public FileDiagnosticsTool(ICodeDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Report what the compiler says about one source file: every error, warning and suggestion with path:line:column. It type-checks in-process and needs no build, so use it to check an edit before dotnet_verify, and read a clean answer as 'this file type-checks', not as 'the project builds'.",
        Schema);

    /// <inheritdoc />
    public ToolSafety Safety => ToolSafety.ReadOnly;

    /// <inheritdoc />
    public async ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        var path = arguments.GetRequiredString("path");

        var result = await _diagnostics.GetDiagnosticsAsync(path, cancellationToken).ConfigureAwait(false);

        if (result.Failure is { } failure)
        {
            return ToolResult.Failure(toolCall.Id, Name, CodeFormat.Failure(failure));
        }

        return CodeFormat.Answer(toolCall.Id, Name, path, Render(path, result.Diagnostics));
    }

    /// <summary>
    /// Renders one file's diagnostics.
    /// </summary>
    /// <param name="path">The path the model asked about.</param>
    /// <param name="diagnostics">The diagnostics, already ordered by line and column.</param>
    /// <returns>
    /// A heading with the counts per severity, then one line per diagnostic as
    /// <c>path:line:column · severity ID · message</c>; the single line <c>path · no diagnostics</c> when clean.
    /// </returns>
    /// <remarks>
    /// A clean file and a check that could not run are different answers, and the wording keeps them apart: this
    /// method is only ever reached on success, so "no diagnostics" cannot be read as a failure.
    /// </remarks>
    internal static string Render(string path, IReadOnlyList<CodeDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (diagnostics.Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{path} · no diagnostics");
        }

        var errors = diagnostics.Count(diagnostic => diagnostic.Severity == CodeSeverity.Error);
        var warnings = diagnostics.Count(diagnostic => diagnostic.Severity == CodeSeverity.Warning);
        var infos = diagnostics.Count(diagnostic => diagnostic.Severity == CodeSeverity.Info);

        var builder = new StringBuilder();
        builder
            .Append(CultureInfo.InvariantCulture, $"{path} · {CodeFormat.Count(errors, "error")}")
            .Append(", ")
            .Append(CodeFormat.Count(warnings, "warning"))
            .Append(", ")
            .Append(CodeFormat.Count(infos, "info", "info"));

        foreach (var diagnostic in diagnostics)
        {
            builder
                .Append("\n  ")
                .Append(diagnostic.Path)
                .Append(':')
                .Append(diagnostic.Line.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(diagnostic.Column.ToString(CultureInfo.InvariantCulture))
                .Append(" · ")
                .Append(CodeFormat.Severity(diagnostic.Severity))
                .Append(' ')
                .Append(diagnostic.Id)
                .Append(" · ")
                .Append(diagnostic.Message);
        }

        return builder.ToString();
    }
}
