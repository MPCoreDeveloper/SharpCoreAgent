using System.Globalization;
using SharpCoreAgent.Domain;

namespace SharpCoreAgent.Application;

/// <summary>
/// Renders the roots a turn may read from: the primary root, then each read-only attachment.
/// </summary>
/// <remarks>
/// <c>list_roots</c> and <c>/roots</c> answer the same question — "what may a path name?" — so they print the
/// same thing from this one function. Two renderers would eventually disagree about a root, and the reader
/// would have no way to tell which of the two was lying.
/// <para>
/// The model-facing form uses <c>/</c> separators like every other tool result and is the one place in the tool
/// surface that prints an absolute path, because naming the legal absolute paths is its whole job. The console
/// form keeps the platform's own separators, because it is a line for a developer's shell rather than for a model.
/// </para>
/// </remarks>
public static class RootListing
{
    /// <summary>Gets the label of the primary root.</summary>
    public const string PrimaryLabel = "primary";

    /// <summary>Gets the label of an attached root.</summary>
    public const string AttachmentLabel = "read-only";

    /// <summary>
    /// The column every path starts in: two spaces past the longer of the two labels, so the roots line up.
    /// </summary>
    private const int LabelWidth = 11;

    /// <summary>
    /// Describes the roots for the model, with <c>/</c> separators and one root per line.
    /// </summary>
    /// <param name="workspace">The workspace whose roots are reported.</param>
    /// <returns>The lines, separated by <c>\n</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> is null.</exception>
    public static string Describe(Workspace workspace) => string.Join('\n', Lines(workspace, modelFacing: true));

    /// <summary>
    /// Describes the roots for the developer, with the platform's own separators.
    /// </summary>
    /// <param name="workspace">The workspace whose roots are reported.</param>
    /// <returns>The lines, separated by the platform's newline.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> is null.</exception>
    public static string DescribeForConsole(Workspace workspace) =>
        string.Join(Environment.NewLine, Lines(workspace, modelFacing: false));

    /// <summary>
    /// Builds one line per root, the primary root first.
    /// </summary>
    /// <param name="workspace">The workspace whose roots are reported.</param>
    /// <param name="modelFacing">Whether paths are rendered with <c>/</c> separators.</param>
    /// <returns>The lines, in the order the roots were named.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="workspace"/> is null.</exception>
    private static string[] Lines(Workspace workspace, bool modelFacing)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return
        [
            Row(PrimaryLabel, workspace.Root, modelFacing),
            .. workspace.Attachments.Select(attachment => Row(AttachmentLabel, attachment.Root, modelFacing)),
        ];
    }

    private static string Row(string label, string root, bool modelFacing) => string.Create(
        CultureInfo.InvariantCulture,
        $"{label.PadRight(LabelWidth)}{(modelFacing ? root.Replace('\\', '/') : root)}");
}
