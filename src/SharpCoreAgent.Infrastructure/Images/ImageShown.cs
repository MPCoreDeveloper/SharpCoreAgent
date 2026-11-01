using SharpCoreAgent.Domain.Conversation;

namespace SharpCoreAgent.Infrastructure.Images;

/// <summary>
/// What it means for a picture to be one the conversation already carries: one predicate and one clause, shared by the
/// three doors that attach one.
/// </summary>
/// <remarks>
/// Three tools can attach an image — <c>view_image</c>, a tool reached through an MCP server, and <c>fetch_url</c> — and
/// each of them can be asked for a picture that is already in the transcript. Attaching it again costs money on every
/// request that follows and shows the model nothing it does not have, so the second copy is skipped and the result says
/// so. The words live here rather than in the three doors so they cannot drift, exactly as
/// <see cref="ImageBudget"/>'s do for the turn's ceiling.
/// <para>
/// Identity is the <see cref="ImageContent"/> as a record: its media type and its bytes. The type is part of it only
/// because a door normalises the spelling before it gets here — <c>image/jpg</c> is attached as <c>image/jpeg</c> — so a
/// payload that compares unequal on its type is one a provider would also be told something different about. Bytes are
/// compared and never the path or the URL: a file can be rewritten between two calls and a page can change, and both of
/// those are new pictures, which is the honest answer.
/// </para>
/// </remarks>
internal static class ImageShown
{
    /// <summary>
    /// Explains, as the end of a sentence, why a picture was not attached a second time.
    /// </summary>
    /// <remarks>
    /// A clause rather than a sentence, because the three doors write three different shapes around it — a success line,
    /// a bracketed note, a header block — and the fact they state is the same one.
    /// </remarks>
    public const string Clause = "the same picture is already in this conversation, attached to an earlier message";

    /// <summary>
    /// Determines whether the conversation already carries this picture.
    /// </summary>
    /// <param name="shown">The pictures the loop said the conversation carries, or <see langword="null"/> when it said nothing.</param>
    /// <param name="picture">The picture the tool is about to attach.</param>
    /// <returns><see langword="true"/> when attaching it would be a second copy.</returns>
    public static bool Already(IReadOnlySet<ImageContent>? shown, ImageContent picture) =>
        shown is not null && shown.Contains(picture);
}
