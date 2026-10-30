using System.Text;
using FSharp.Compiler.EditorServices;
using FSharp.Compiler.Text;

namespace SharpCoreAgent.Languages.FSharp;

/// <summary>
/// Turns the checker's structured tooltip into one line of plain text.
/// </summary>
/// <remarks>
/// The checker does not hand out a signature string: it hands out a tree of tagged fragments, because an editor
/// colours them. A model wants the characters, so the fragments are concatenated in order and the tags dropped.
/// <para>
/// A composition error — the checker's way of saying "this type does not exist" — is rendered as its own message
/// rather than skipped, because an empty signature beside a real problem reads as "no signature here".
/// </para>
/// </remarks>
internal static class ToolTipTextRenderer
{
    /// <summary>
    /// Renders a tooltip as a single line.
    /// </summary>
    /// <param name="text">The tooltip, or <see langword="null"/>.</param>
    /// <returns>The text with line breaks flattened, or an empty string when there is nothing to show.</returns>
    public static string Render(ToolTipText? text)
    {
        if (text is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var element in text.Item)
        {
            if (element is ToolTipElement.Group group)
            {
                foreach (var data in group.elements)
                {
                    foreach (var part in data.MainDescription)
                    {
                        builder.Append(part.Text);
                    }
                }
            }
            else if (element is ToolTipElement.CompositionError error)
            {
                builder.Append(error.errorText);
            }
        }

        return builder.ToString().ReplaceLineEndings(" ").Trim();
    }

    /// <summary>
    /// Renders a run of tagged fragments as one line.
    /// </summary>
    /// <param name="parts">The fragments, or <see langword="null"/>.</param>
    /// <returns>The concatenated text with line breaks flattened.</returns>
    /// <remarks>
    /// A parameter's label and a return type arrive as bare fragment arrays rather than as a tooltip, so they need
    /// the same flattening without the union wrapper.
    /// </remarks>
    public static string Render(TaggedText[]? parts) =>
        parts is null
            ? string.Empty
            : string.Concat(parts.Select(static part => part.Text)).ReplaceLineEndings(" ").Trim();
}
