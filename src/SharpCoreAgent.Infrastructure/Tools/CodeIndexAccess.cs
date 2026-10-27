using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Index;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// The one place that decides whether the index can answer, and what the model is told when it cannot.
/// </summary>
/// <remarks>
/// Five tools ask the same three questions — is there an index, is it usable, is anyone else writing it — and the
/// answers must not drift between them. Keeping the wording here is also what makes the failure messages in
/// <c>contracts/retrieval-tools.md</c> a property of the code: a test asserts one constant instead of five literals.
/// <para>
/// A missing index and a corrupt one are failures because the model can act on them (run the command, fall back to
/// <c>search_text</c>); a busy index is only a note, because the last complete build still answers.
/// </para>
/// </remarks>
internal static class CodeIndexAccess
{
    /// <summary>The failure text for a workspace that has never been indexed.</summary>
    internal const string MissingMessage = "the code index is missing for this workspace; run `sharpcoreagent index`";

    /// <summary>The failure text when the host started with <c>--no-index</c>.</summary>
    internal const string DisabledMessage = "the code index is disabled (--no-index); use search_text and read_file instead";

    /// <summary>The failure text for a database that was rejected on open.</summary>
    internal const string CorruptMessage = "the index is corrupt and will be rebuilt; run `sharpcoreagent index`";

    /// <summary>The note printed beside an answer that was read while another process was building.</summary>
    internal const string BusyNote = "index busy (another process is indexing)";

    /// <summary>
    /// Resolves the index and reports whether a question may be asked of it.
    /// </summary>
    /// <param name="index">The registered index, or <see langword="null"/> when the host disabled it.</param>
    /// <param name="callId">The tool call being answered.</param>
    /// <param name="toolName">The tool name, echoed back.</param>
    /// <param name="workspaceRoot">The workspace the status describes when there is no index at all.</param>
    /// <param name="cancellationToken">Cancels the status read.</param>
    /// <returns>
    /// A failure to return as-is, or the status the caller renders beside its answer. The pair is a tuple rather than
    /// two calls so no tool can read the status without also having been told it may.
    /// </returns>
    internal static async ValueTask<(ToolResult? Failure, IndexStatus Status)> CheckAsync(
        ICodeIndex? index,
        string callId,
        string toolName,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        if (index is null)
        {
            return (ToolResult.Failure(callId, toolName, DisabledMessage), IndexStatus.Missing(workspaceRoot));
        }

        var status = await index.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        return status.Freshness switch
        {
            IndexFreshness.Missing => (ToolResult.Failure(callId, toolName, MissingMessage), status),
            IndexFreshness.Corrupt => (ToolResult.Failure(callId, toolName, CorruptMessage), status),
            _ => (null, status),
        };
    }

    /// <summary>
    /// Builds a successful answer, shortening it to the tool's own character budget.
    /// </summary>
    /// <param name="callId">The tool call being answered.</param>
    /// <param name="toolName">The tool name, echoed back.</param>
    /// <param name="content">The rendered answer.</param>
    /// <param name="maxChars">The character budget for this tool.</param>
    /// <returns>The result, with <see cref="ToolResult.IsTruncated"/> set when the budget cut it.</returns>
    /// <remarks>
    /// The effect is always <see cref="ToolEffectKind.Read"/> at <c>.</c>: every one of these tools answers from a
    /// structure derived from the workspace, so the workspace is the target and no file was touched.
    /// </remarks>
    internal static ToolResult Answer(string callId, string toolName, string content, int maxChars)
    {
        var text = TextLimits.Bound(content, maxChars, out var truncated);

        return ToolResult.Success(callId, toolName, text, new ToolEffect(ToolEffectKind.Read, ".")) with
        {
            IsTruncated = truncated,
        };
    }

    /// <summary>
    /// Validates a <c>kind</c> argument against the closed list the index can answer for.
    /// </summary>
    /// <param name="value">The argument as the model wrote it, or <see langword="null"/>.</param>
    /// <param name="allowed">The closed list, in schema order.</param>
    /// <param name="toolName">The tool name, used in the error message.</param>
    /// <param name="field">The parameter name, used in the error message.</param>
    /// <returns>The canonical spelling, or <see langword="null"/> for "any kind".</returns>
    /// <exception cref="ToolArgumentException">The value is not one of <paramref name="allowed"/>.</exception>
    /// <remarks>
    /// The check is here rather than delegated to <see cref="CodeQuery.Create"/> so the error names the JSON field the
    /// model actually wrote, which is the detail that makes a correction possible on the next call.
    /// </remarks>
    internal static string? KindName(string? value, IReadOnlyList<string> allowed, string toolName, string field)
    {
        ArgumentNullException.ThrowIfNull(allowed);

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (allowed.FirstOrDefault(candidate => string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
            is not { } canonical)
        {
            throw new ToolArgumentException(
                toolName,
                field,
                $"must be one of {string.Join(", ", allowed)}, but was '{trimmed}'");
        }

        return string.Equals(canonical, CodeQuery.AnyKind, StringComparison.Ordinal) ? null : canonical;
    }

    /// <summary>
    /// Resolves a path filter through the workspace and renders it the way the index stores it.
    /// </summary>
    /// <param name="path">The argument as the model wrote it, or <see langword="null"/>.</param>
    /// <param name="workspace">The workspace the invocation may touch.</param>
    /// <param name="callId">The tool call being answered.</param>
    /// <param name="toolName">The tool name, echoed back.</param>
    /// <param name="refusal">The refusal to return when the path escapes the root.</param>
    /// <returns>The workspace-relative filter, or <see langword="null"/> for the whole workspace.</returns>
    /// <remarks>
    /// The filter is resolved by the workspace and not by the index because containment is a property of the turn, not
    /// of the store: an index built over a wider tree must still refuse a path this turn may not read.
    /// </remarks>
    internal static string? ResolvePath(
        string? path,
        Workspace workspace,
        string callId,
        string toolName,
        out ToolResult? refusal)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        refusal = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (!workspace.TryResolve(path.Trim(), out var fullPath, out var error))
        {
            refusal = ToolResult.Refused(callId, toolName, error ?? $"path '{path}' is not inside the workspace.");
            return null;
        }

        var relative = workspace.ToRelative(fullPath);
        return string.Equals(relative, ".", StringComparison.Ordinal) ? null : relative;
    }
}
