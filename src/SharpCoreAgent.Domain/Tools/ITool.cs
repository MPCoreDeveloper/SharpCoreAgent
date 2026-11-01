using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Model;

namespace SharpCoreAgent.Domain.Tools;

/// <summary>
/// What a tool is allowed to know about the turn that invoked it.
/// </summary>
/// <param name="Workspace">The validated workspace root; every path argument is resolved through it.</param>
/// <remarks>
/// <see cref="Output"/> is the only channel a tool may use to say something before it returns. It is
/// null when nobody is listening, which is the normal case in tests and in <c>--quiet</c> runs.
/// <para>
/// <see cref="Model"/> and <see cref="Approval"/> exist for the tools that hand work to something else --
/// a sub-agent, a child process -- and must therefore describe the conversation rather than the host's
/// startup settings. Both default to the safest reading: no model, and a mode in which nothing may be
/// written without asking.
/// </para>
/// </remarks>
public sealed record ToolInvocationContext(Workspace Workspace)
{
    /// <summary>Gets the sink that narrates a long-running invocation, when one was attached.</summary>
    public IToolOutputSink? Output { get; init; }

    /// <summary>Gets the model the conversation is using, or null when the caller did not say.</summary>
    public ModelDescriptor? Model { get; init; }

    /// <summary>
    /// Gets how many more bytes of picture this turn may attach, or null when the caller did not say.
    /// </summary>
    /// <remarks>
    /// A tool that answers with an image reads this before it attaches one, and nothing else on the context is
    /// affected by it: the count is the loop's, because the loop is the only place that sees a whole turn, and a
    /// picture is re-sent on every request that follows it rather than once. Null is the documented "no ceiling was
    /// stated" and the safe default for a context built by hand, exactly as <see cref="Model"/> is for the capability
    /// gate. A tool that receives zero attaches nothing and says so — silence would leave the model describing a
    /// picture it was never shown.
    /// </remarks>
    public long? ImageBytesRemaining { get; init; }

    /// <summary>
    /// Gets the pictures the conversation already carries, or null when the caller did not say.
    /// </summary>
    /// <remarks>
    /// A tool that answers with an image reads this before it attaches one, because an attached picture is re-sent on
    /// every request that follows it: a conversation that shows one screenshot on each of ten rounds pays for ten copies
    /// of the same bytes, and the model learns nothing from the ninth. Identity is the picture itself — its type and its
    /// bytes — rather than the name it arrived under, because two URLs can serve one picture and one path can hold
    /// different bytes by the time it is asked for a second time.
    /// <para>
    /// Null means the caller stated nothing, so a context built by hand in a test attaches and a duplicate is never worse
    /// than a missing picture — the same safe default <see cref="ImageBytesRemaining"/> and <see cref="Model"/> take. An
    /// empty set is the loop saying it looked and the conversation holds no picture yet.
    /// </para>
    /// </remarks>
    public IReadOnlySet<ImageContent>? ImagesAlreadyShown { get; init; }

    /// <summary>
    /// Gets the approval mode in force for the conversation. Default <see cref="ApprovalMode.ReadOnly"/>.
    /// </summary>
    /// <remarks>
    /// A tool that runs a nested agent reads this to decide what the nested one may do. Read-only is the
    /// default because a context built by hand -- in a test, or by a host that never filled it in -- must
    /// not be permission to write.
    /// </remarks>
    public ApprovalMode Approval { get; init; } = ApprovalMode.ReadOnly;
}

/// <summary>
/// A capability the model can invoke: a name, a schema, a safety class and an implementation.
/// </summary>
/// <remarks>
/// Implementations are synchronous-minded and short-lived: they do their work, describe what they
/// changed and return. They never prompt, never retry and never decide whether they are allowed --
/// approval is decided by the agent loop before <see cref="ExecuteAsync"/> is called.
/// </remarks>
public interface ITool
{
    /// <summary>Gets the declaration advertised to the model.</summary>
    ToolDefinition Definition { get; }

    /// <summary>Gets how carefully this tool must be approved.</summary>
    ToolSafety Safety { get; }

    /// <summary>
    /// Executes one invocation.
    /// </summary>
    /// <param name="toolCall">The model's call, including raw argument JSON.</param>
    /// <param name="context">The workspace the invocation may touch.</param>
    /// <param name="cancellationToken">Cancels the invocation; must be honoured by long-running tools.</param>
    /// <returns>The result to feed back to the model. Failures are returned, not thrown.</returns>
    ValueTask<ToolResult> ExecuteAsync(ToolCall toolCall, ToolInvocationContext context, CancellationToken cancellationToken);
}
