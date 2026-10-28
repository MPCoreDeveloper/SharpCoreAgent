using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Explains what surrounds one declaration or one file: the centre, its declarations, its neighbours and the passage.
/// </summary>
/// <remarks>
/// This is the "orient me" tool. A search answers a question and a symbol lookup answers a name; this one answers "what
/// is this thing part of", which is the question a developer asks before an edit. It is also the only retrieval tool that
/// accepts a path, because "what is in this file and what does it talk to" is the same question at a coarser grain.
/// </remarks>
public sealed class CodeContextTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "code_context";

    private const string Schema =
        """{"type":"object","properties":{"target":{"type":"string","description":"A workspace-relative path, or a symbol name."},"hops":{"type":"integer","minimum":1,"maximum":3,"description":"How many graph hops to follow. Defaults to 1."},"k":{"type":"integer","minimum":1,"maximum":12,"description":"How many neighbouring declarations to show. Defaults to 8."}},"required":["target"],"additionalProperties":false}""";

    private readonly ICodeIndex? _index;

    /// <summary>
    /// Initialises the tool.
    /// </summary>
    /// <param name="index">The read side of the code index, or <see langword="null"/> when the host disabled it.</param>
    public CodeContextTool(ICodeIndex? index = null) => _index = index;

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Explain what sits around a declaration or a file: the declarations it holds, its strongest typed neighbours with weights, and the passage it is declared in. Use it before editing something to see who depends on it.",
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
        var target = arguments.GetRequiredString("target");
        var hops = arguments.GetOptionalInt("hops", 1, minimum: 1, maximum: CodeQuery.MaxHops);
        var k = arguments.GetOptionalInt("k", CodeQuery.DefaultK, minimum: 1, maximum: CodeQuery.MaxK);

        var resolved = target;

        if (LooksLikePath(target))
        {
            var filter = CodeIndexAccess.ResolvePath(target, context.Workspace, toolCall.Id, Name, out var refusal);

            if (refusal is not null)
            {
                return refusal;
            }

            resolved = filter ?? target.Trim();
        }

        var (failure, _) = await CodeIndexAccess
            .CheckAsync(_index, toolCall.Id, Name, context.Workspace.Root, cancellationToken)
            .ConfigureAwait(false);

        if (failure is not null)
        {
            return failure;
        }

        var answer = await _index!.GetContextAsync(resolved, hops, k, cancellationToken).ConfigureAwait(false);

        return CodeIndexAccess.Answer(toolCall.Id, Name, Render(answer, hops), CodeIndexFormat.MaxOutputChars);
    }

    /// <summary>
    /// Decides whether the target names a file rather than a declaration.
    /// </summary>
    /// <param name="target">The target as the model wrote it.</param>
    /// <returns><see langword="true"/> when the text contains a path separator.</returns>
    /// <remarks>
    /// A separator is the whole test. Resolving every target through the workspace would turn <c>CliHost.RunAsync</c>
    /// into a path that happens not to exist and silently lose the symbol; a name without a separator can still be a
    /// path, and the index resolves that case itself.
    /// </remarks>
    private static bool LooksLikePath(string target) =>
        target.Contains('/', StringComparison.Ordinal) || target.Contains('\\', StringComparison.Ordinal);

    /// <summary>
    /// Renders one context answer.
    /// </summary>
    /// <param name="context">The answer the index produced.</param>
    /// <param name="hops">The depth that was asked for, so the heading can repeat it.</param>
    /// <returns>
    /// The file's declarations when the target was a path, the subject's identity, the weighted neighbourhoods and the
    /// passage that holds the subject.
    /// </returns>
    /// <remarks>
    /// The neighbour count is printed as "8 shown of 26" with the weight floor beside it, because a bare list of eight
    /// reads as "this is all there is". The passage comes last: it is the longest part of the answer, and a model that
    /// only needed the neighbours should not have to read past forty lines of source to find them.
    /// </remarks>
    internal static string Render(CodeContext context, int hops)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Subject is null && context.Declarations.Count == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"nothing in the index matches '{context.Target}' (try find_symbol or search_code)");
        }

        var builder = new StringBuilder();

        if (context.Declarations.Count > 0)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{context.Target} · {context.Declarations.Count} {(context.Declarations.Count == 1 ? "declaration" : "declarations")}");

            foreach (var declaration in context.Declarations)
            {
                builder
                    .Append("\n  ")
                    .Append(declaration.StartLine.ToString(CultureInfo.InvariantCulture))
                    .Append('-')
                    .Append(declaration.EndLine.ToString(CultureInfo.InvariantCulture))
                    .Append(" · ")
                    .Append(declaration.Kind)
                    .Append(" · ")
                    .Append(declaration.Name);
            }
        }

        if (context.Subject is { } subject)
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder
                .Append(subject.QualifiedName)
                .Append(" · ")
                .Append(subject.Kind)
                .Append(" · ")
                .Append(subject.Path)
                .Append(':')
                .Append(subject.StartLine.ToString(CultureInfo.InvariantCulture))
                .Append('-')
                .Append(subject.EndLine.ToString(CultureInfo.InvariantCulture));

            if (!string.IsNullOrEmpty(subject.Community))
            {
                builder.Append(" · community ").Append(subject.Community);
            }

            builder.Append("\n\n");

            if (context.Neighbours.Count == 0)
            {
                builder.Append(CultureInfo.InvariantCulture, $"neighbours (hops {hops}, none)");
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $"neighbours (hops {hops}, {context.Neighbours.Count} shown of {context.Considered}");

                if (context.MinWeight > 0)
                {
                    builder.Append(CultureInfo.InvariantCulture, $", min weight {context.MinWeight:0.00}");
                }

                builder.Append(')');

                foreach (var neighbour in context.Neighbours)
                {
                    builder
                        .Append("\n  ")
                        .Append((neighbour.Relation ?? "related").PadRight(11))
                        .Append(neighbour.Symbol ?? neighbour.Path)
                        .Append(" · ")
                        .Append(neighbour.Path)
                        .Append(':')
                        .Append(neighbour.StartLine.ToString(CultureInfo.InvariantCulture))
                        .Append('-')
                        .Append(neighbour.EndLine.ToString(CultureInfo.InvariantCulture))
                        .Append(CultureInfo.InvariantCulture, $" · w {neighbour.Score:0.00}");
                }
            }
        }

        if (context.Focus is { } focus)
        {
            builder
                .Append("\n\n")
                .Append(CodeIndexFormat.Excerpt(focus, CodeIndexFormat.FocusExcerptLines));
        }

        return builder.ToString();
    }
}
