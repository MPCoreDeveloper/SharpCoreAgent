using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Finds declarations by name, which is the cheapest way to get from a word the model read to a citable location.
/// </summary>
/// <remarks>
/// The answer is a symbol table, not a passage list: one line per declaration with its path, its line range, its degree
/// and its community. That is what makes the next call possible — <c>find_references</c> takes exactly the qualified
/// name printed here, and <c>code_context</c> takes it too, so the three tools chain without the model guessing a key.
/// </remarks>
public sealed class FindSymbolTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "find_symbol";

    private const int MaxResults = 50;

    /// <summary>Gets the kinds a symbol search may be restricted to, in schema order.</summary>
    private static readonly string[] KindNames =
    [
        CodeQuery.AnyKind,
        "class",
        "interface",
        "record",
        "struct",
        "enum",
        "method",
        "property",
        "field",
    ];

    private const string Schema =
        """{"type":"object","properties":{"name":{"type":"string","description":"Symbol name, qualified name or a fragment of one (case-insensitive)."},"kind":{"type":"string","enum":["any","class","interface","record","struct","enum","method","property","field"],"description":"Restrict to one declaration kind. Defaults to any."},"max_results":{"type":"integer","minimum":1,"maximum":50,"description":"Defaults to 20."}},"required":["name"],"additionalProperties":false}""";

    private readonly ICodeIndex? _index;

    /// <summary>
    /// Initialises the tool.
    /// </summary>
    /// <param name="index">The read side of the code index, or <see langword="null"/> when the host disabled it.</param>
    public FindSymbolTool(ICodeIndex? index = null) => _index = index;

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Find declarations (classes, methods, properties, fields) by name. Returns qualified names with path:start-end, caller and callee counts, and the community label. Use it when you know the name you are looking for.",
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
        var name = arguments.GetRequiredString("name");
        var kind = CodeIndexAccess.KindName(arguments.GetOptionalString("kind"), KindNames, Name, "kind");
        var maxResults = arguments.GetOptionalInt("max_results", 20, minimum: 1, maximum: MaxResults);

        var (failure, status) = await CodeIndexAccess
            .CheckAsync(_index, toolCall.Id, Name, context.Workspace.Root, cancellationToken)
            .ConfigureAwait(false);

        if (failure is not null)
        {
            return failure;
        }

        var symbols = await _index!.FindSymbolsAsync(name, kind, cancellationToken).ConfigureAwait(false);
        var take = Math.Min(maxResults, MaxResults);

        return CodeIndexAccess.Answer(
            toolCall.Id,
            Name,
            Render(name, status, symbols, take),
            CodeIndexFormat.MaxOutputChars);
    }

    /// <summary>
    /// Renders one symbol answer.
    /// </summary>
    /// <param name="name">The name that was searched for, as the model wrote it.</param>
    /// <param name="status">The index the answer came from.</param>
    /// <param name="symbols">The matches, already ordered by the index.</param>
    /// <param name="max">How many to print.</param>
    /// <returns>A heading with the match-tier counts, then one entry per declaration.</returns>
    /// <remarks>
    /// The tier counts are printed because they say how the name was interpreted: <c>exact 1</c> means the model asked
    /// for a declaration that exists, while <c>partial 12</c> means it asked for a fragment and got guesses. A model
    /// that can see the difference can decide whether to refine the name instead of trusting the first line.
    /// </remarks>
    internal static string Render(string name, IndexStatus status, IReadOnlyList<CodeSymbolRef> symbols, int max)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(symbols);

        if (symbols.Count == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"no symbol named '{name}' (searched {CodeIndexFormat.Number(status.Symbols)} declarations)");
        }

        var exact = symbols.Count(symbol => Tier(symbol, name) == 0);
        var ignoringCase = symbols.Count(symbol => Tier(symbol, name) == 1);
        var partial = symbols.Count - exact - ignoringCase;

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{symbols.Count} {(symbols.Count == 1 ? "symbol" : "symbols")} for '{name}'");
        builder.Append(CultureInfo.InvariantCulture, $" (exact {exact}, ignore-case {ignoringCase}, partial {partial})");

        var shown = Math.Min(Math.Max(max, 0), symbols.Count);

        for (var index = 0; index < shown; index++)
        {
            var symbol = symbols[index];

            builder
                .Append("\n\n")
                .Append(index + 1)
                .Append(") ")
                .Append(symbol.QualifiedName)
                .Append(" · ")
                .Append(symbol.Kind)
                .Append(" · ")
                .Append(symbol.Path)
                .Append(':')
                .Append(symbol.StartLine.ToString(CultureInfo.InvariantCulture))
                .Append('-')
                .Append(symbol.EndLine.ToString(CultureInfo.InvariantCulture));

            builder.Append("\n   ");

            if (Container(symbol) is { } container)
            {
                builder.Append(container).Append(" · ");
            }

            builder.Append(CultureInfo.InvariantCulture, $"callers {symbol.Callers} · callees {symbol.Callees}");

            if (!string.IsNullOrEmpty(symbol.Community))
            {
                builder.Append(" · community ").Append(symbol.Community);
            }

            if (CodeIndexFormat.Flatten(symbol.Signature) is { Length: > 0 } signature)
            {
                builder.Append("\n   ").Append(signature);
            }
        }

        if (shown < symbols.Count)
        {
            builder.Append(CultureInfo.InvariantCulture, $"\n\n… {symbols.Count - shown} more not shown (raise max_results)");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Classifies a match against the searched name, so the heading can count the tiers.
    /// </summary>
    /// <param name="symbol">The declaration.</param>
    /// <param name="name">The searched name.</param>
    /// <returns><c>0</c> for an exact match, <c>1</c> for a case-insensitive one, <c>2</c> for a partial one.</returns>
    private static int Tier(CodeSymbolRef symbol, string name)
    {
        if (Matches(symbol, name, StringComparison.Ordinal))
        {
            return 0;
        }

        return Matches(symbol, name, StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    }

    private static bool Matches(CodeSymbolRef symbol, string name, StringComparison comparison) =>
        string.Equals(symbol.Name, name, comparison) ||
        string.Equals(symbol.QualifiedName, name, comparison) ||
        string.Equals(symbol.Key, name, comparison);

    /// <summary>
    /// Renders the type a member is declared in, for the one line that says how connected it is.
    /// </summary>
    /// <param name="symbol">The declaration.</param>
    /// <returns>The declaring type, or <see langword="null"/> for a type or namespace-level declaration.</returns>
    /// <remarks>
    /// A member's qualified name ends in the type and then the member, so dropping the last segment gives the type. A
    /// type's own qualified name ends in a namespace segment, which is not a container worth printing, so those
    /// declarations get no prefix at all.
    /// </remarks>
    private static string? Container(CodeSymbolRef symbol)
    {
        if (symbol.Kind is not ("method" or "property" or "field"))
        {
            return null;
        }

        var separator = symbol.QualifiedName.LastIndexOf('.');
        return separator <= 0 ? null : symbol.QualifiedName[..separator];
    }
}
