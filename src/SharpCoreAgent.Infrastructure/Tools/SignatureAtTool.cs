using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Code;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Code;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Reports what a callable takes at a call site, including which argument the position is inside.
/// </summary>
/// <remarks>
/// A signature is the one answer a model cannot derive from reading the file: a curried F# function's type, its
/// argument names and which overload the argument types selected all live in the resolved symbol, not in the text.
/// That is why this asks the language service, and why the answer is plural — an overloaded call site has more than one
/// honest signature, and the one the position resolves to is marked rather than assumed to be first.
/// <para>
/// The active argument is the index the reader needs to stop guessing: at <c>f (a, b|)</c> the second parameter is the
/// one under the cursor, and a tool that always named the first would be wrong more often than right.
/// </para>
/// </remarks>
public sealed class SignatureAtTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "signature_at";

    private const string Schema =
        """{"type":"object","properties":{"path":{"type":"string","description":"Workspace-relative path to the source file holding the call."},"line":{"type":"integer","minimum":1,"description":"1-based line of the call site."},"column":{"type":"integer","minimum":0,"description":"0-based column of any character inside the name being called."}},"required":["path","line","column"],"additionalProperties":false}""";

    private readonly ISignatureHelp _signatures;

    /// <summary>
    /// Initialises the tool.
    /// </summary>
    /// <param name="signatures">The code-intelligence surface this host registered.</param>
    /// <exception cref="ArgumentNullException"><paramref name="signatures"/> is null.</exception>
    public SignatureAtTool(ISignatureHelp signatures)
    {
        ArgumentNullException.ThrowIfNull(signatures);

        _signatures = signatures;
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Show what a callable takes at a call site: every signature that applies, which one the position resolves to, and which argument the position is inside. Use it before writing a call, rather than guessing an argument's name or order.",
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

        var result = await _signatures.GetSignatureAsync(path, line, column, cancellationToken).ConfigureAwait(false);

        if (result.Failure is { } failure)
        {
            return ToolResult.Failure(toolCall.Id, Name, CodeFormat.Failure(failure));
        }

        return CodeFormat.Answer(
            toolCall.Id,
            Name,
            path,
            Render(path, line, column, result.Signatures, result.ActiveSignature));
    }

    /// <summary>
    /// Renders the signatures at one position.
    /// </summary>
    /// <param name="path">The path the model asked about.</param>
    /// <param name="line">The 1-based line it asked about.</param>
    /// <param name="column">The 0-based column it asked about.</param>
    /// <param name="signatures">The signatures, most specific first.</param>
    /// <param name="activeSignature">The index the position resolves to, or <c>-1</c> when empty.</param>
    /// <returns>
    /// A heading naming the position and the active signature, then one numbered block per signature with its
    /// arguments, and <c>← active</c> beside the argument the position sits inside.
    /// </returns>
    /// <remarks>
    /// An out-of-range index is treated as the first signature rather than as an error: the list is the answer, and the
    /// index only decides where a marker is drawn.
    /// </remarks>
    internal static string Render(
        string path,
        int line,
        int column,
        IReadOnlyList<SignatureInfo> signatures,
        int activeSignature)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(signatures);

        var position = string.Create(CultureInfo.InvariantCulture, $"{path}:{line}:{column}");

        if (signatures.Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{position} · nothing callable at this position");
        }

        var active = activeSignature >= 0 && activeSignature < signatures.Count ? activeSignature : 0;

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{position} · {CodeFormat.Count(signatures.Count, "signature")} · active {active + 1} of {signatures.Count}");

        for (var index = 0; index < signatures.Count; index++)
        {
            var signature = signatures[index];
            builder
                .Append("\n  ")
                .Append((index + 1).ToString(CultureInfo.InvariantCulture))
                .Append(") ")
                .Append(signature.Display);

            for (var parameter = 0; parameter < signature.Parameters.Count; parameter++)
            {
                builder.Append("\n     ").Append(signature.Parameters[parameter]);

                if (index == active && parameter == signature.ActiveParameter)
                {
                    builder.Append(" ← active");
                }
            }
        }

        return builder.ToString();
    }
}
