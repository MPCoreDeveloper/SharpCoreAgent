namespace SharpCoreAgent.Domain.Tools;

/// <summary>
/// Where a long-running tool narrates what it is doing while it is still doing it.
/// </summary>
/// <remarks>
/// Without this a command is a black box: the model waits, the developer sees nothing, and a build that
/// takes forty seconds looks like a hang. A tool writes the same lines it will return, as they arrive.
/// <para>
/// A sink is optional on purpose. A tool must run identically when nobody is listening, and the text it
/// returns must not depend on whether a sink was attached, so implementations bound what they forward
/// and never let a rendering failure change the result.
/// </para>
/// </remarks>
public interface IToolOutputSink
{
    /// <summary>
    /// Forwards one increment of a tool's output.
    /// </summary>
    /// <param name="text">The text to show; it may end mid-line.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the text has been rendered.</returns>
    ValueTask WriteAsync(string text, CancellationToken cancellationToken);
}
