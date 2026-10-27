using SharpCoreAgent.Domain.Conversation;

namespace SharpCoreAgent.Domain.Tools;

/// <summary>
/// A tool that can describe what a call would change before it is allowed to change it.
/// </summary>
/// <remarks>
/// Approval without a preview asks the developer to trust a JSON line. A tool that implements this
/// interface hands the approver the artefact the developer actually needs -- for a file edit, a unified
/// diff -- so <c>allow replace_in_file?</c> is answered by reading the change instead of the arguments.
/// <para>
/// The contract is deliberately forgiving: a preview is a courtesy, so a tool reports
/// <see langword="null"/> when it cannot produce one (a missing file, an argument it could not parse).
/// Nothing here may decide approval, execute anything or throw for a condition the tool would report as
/// a <see cref="ToolResult"/>; the loop calls this before <see cref="ITool.ExecuteAsync"/> and treats a
/// failure as "no preview".
/// </para>
/// </remarks>
public interface IToolPreview
{
    /// <summary>
    /// Describes the change the call would make, without making it.
    /// </summary>
    /// <param name="toolCall">The model's call, including raw argument JSON.</param>
    /// <param name="context">The workspace the call would touch.</param>
    /// <param name="cancellationToken">Cancels the preview.</param>
    /// <returns>
    /// A short, human-readable description -- typically a unified diff of at most a few dozen lines --
    /// or <see langword="null"/> when no preview can be produced.
    /// </returns>
    ValueTask<string?> DescribeChangeAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken);
}
