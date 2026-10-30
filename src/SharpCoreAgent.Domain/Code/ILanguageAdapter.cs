namespace SharpCoreAgent.Domain.Code;

/// <summary>
/// Tells the router which files one language's code intelligence covers.
/// </summary>
/// <remarks>
/// Kept apart from the three ports so choosing an adapter costs one string comparison instead of a probe: an
/// implementation answers from the path alone, never from the file's contents or from the workspace's shape.
/// <para>
/// This is also why a language that gains code intelligence later needs no change in the tools. They ask the
/// router, the router asks every registered adapter, and the first one that says yes answers.
/// </para>
/// </remarks>
public interface ILanguageAdapter
{
    /// <summary>
    /// Reports whether this adapter can answer for a path.
    /// </summary>
    /// <param name="path">A workspace-relative path, or a file name whose extension is what matters.</param>
    /// <returns><see langword="true"/> when the adapter's language claims the file.</returns>
    bool Supports(string path);
}
