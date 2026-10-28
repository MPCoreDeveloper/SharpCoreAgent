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
