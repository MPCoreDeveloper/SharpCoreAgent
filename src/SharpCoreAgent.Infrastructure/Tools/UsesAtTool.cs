using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Code;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Code;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Finds every place the compiler bound a symbol to the same declaration, starting from the position of one occurrence.
/// </summary>
/// <remarks>
/// The difference from <c>find_references</c> is what makes this worth a tool of its own: the index matches names, so
/// it cannot tell two <c>Total</c> members on different types apart and it counts a string that spells the name. This
/// asks the language service, which knows which declaration the token at that position means — including which of two
/// same-named members an <c>open</c> or an overload resolution selected.
/// <para>
/// The position is the point of the call, so the arguments are the position: a line and a column are what a reader
/// already has from a diagnostic or an editor, and neither is guessed from a search.
/// </para>
/// </remarks>
public sealed class UsesAtTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "uses_at";

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"Workspace-relative path to the source file holding an occurrence of the symbol."},"line":{"type":"integer","minimum":1,"description":"1-based line of that occurrence."},"column":{"type":"integer","minimum":0,"description":"0-based column of any character inside the symbol's name."}},"required":["path","line","column"],"additionalProperties":false}""";

    private readonly ISymbolUses _uses;

    /// <summary>
    /// Initialises the tool.
    /// </summary>
    /// <param name="uses">The code-intelligence surface this host registered.</param>
    /// <exception cref="ArgumentNullException"><paramref name="uses"/> is null.</exception>
    public UsesAtTool(ISymbolUses uses)
    {
        ArgumentNullException.ThrowIfNull(uses);

        _uses = uses;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Find everywhere the compiler bound a symbol to the same declaration, given the line and column of one occurrence. The declaration is included and marked. Unlike find_references this is not a name search: it excludes same-named members on other types, strings and comments.",
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
        var line = arguments.GetRequiredInt("line", minimum: 1);
        var column = arguments.GetRequiredInt("column", minimum: 0);

        var result = await _uses.FindUsesAsync(path, line, column, cancellationToken).ConfigureAwait(false);

        if (result.Failure is { } failure)
        {
            return ToolResult.Failure(toolCall.Id, Name, CodeFormat.Failure(failure));
        }

        return CodeFormat.Answer(toolCall.Id, Name, path, Render(path, line, column, result.Uses));
    }

    /// <summary>
    /// Renders one symbol's uses.
    /// </summary>
    /// <param name="path">The path the model asked about.</param>
    /// <param name="line">The 1-based line it asked about.</param>
    /// <param name="column">The 0-based column it asked about.</param>
    /// <param name="uses">The uses, ordered by path, line and column, declaration first in its own file.</param>
    /// <returns>
    /// A heading with the symbol, the position and the file count, then one line per use as
    /// <c>definition path:line:column · display</c> or <c>use …</c>.
    /// </returns>
    /// <remarks>
    /// The label on each line is the whole reason the answer is readable as a plan: a reader who is about to change a
    /// signature needs to know which of the sites is the one that would move.
    /// </remarks>
    internal static string Render(string path, int line, int column, IReadOnlyList<SymbolUse> uses)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(uses);

        var position = string.Create(CultureInfo.InvariantCulture, $"{path}:{line}:{column}");

        if (uses.Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{position} · no uses");
        }

        var files = uses
            .Select(use => use.Path)
            .Distinct(StringComparer.Ordinal)
            .Count();

        var builder = new StringBuilder();
        builder
            .Append(CultureInfo.InvariantCulture, $"{uses[0].Display} at {position} · {CodeFormat.Count(uses.Count, "use")}")
            .Append(" · ")
            .Append(CodeFormat.Count(files, "file"));

        foreach (var use in uses)
        {
            builder
                .Append("\n  ")
                .Append(use.IsDefinition ? "definition " : "use ")
                .Append(use.Path)
                .Append(':')
                .Append(use.Line.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(use.Column.ToString(CultureInfo.InvariantCulture))
                .Append(" · ")
                .Append(use.Display);
        }

        return builder.ToString();
    }
}
