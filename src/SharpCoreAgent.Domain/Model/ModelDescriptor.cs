namespace SharpCoreAgent.Domain.Model;

/// <summary>
/// Why a provider stopped generating.
/// </summary>
public enum FinishReason
{
    /// <summary>The provider did not say, or said something this client does not recognise.</summary>
    Unknown,

    /// <summary>The model wrote a complete answer.</summary>
    Stop,

    /// <summary>The model hit a length limit and the answer may be cut off.</summary>
    Length,

    /// <summary>The model requested tool calls and is waiting for results.</summary>
    ToolCalls,

    /// <summary>The provider filtered the output.</summary>
    ContentFilter,
}

/// <summary>
/// Everything the agent needs to know about a model before it streams a turn.
/// </summary>
/// <param name="Provider">The adapter key: <c>deepseek</c>, <c>grok</c>, <c>openai-compatible</c> or <c>demo</c>.</param>
/// <param name="Id">The provider's model identifier, sent on the wire verbatim.</param>
/// <param name="DisplayName">The name shown in the CLI.</param>
/// <param name="ContextWindow">The advertised context window in tokens, when known.</param>
/// <param name="SupportsTools">Whether the model can emit tool calls at all.</param>
/// <param name="SupportsStreaming">Whether the provider implements server-sent events for this model.</param>
/// <param name="EmitsThinking">Whether the provider streams reasoning deltas separately from text.</param>
/// <remarks>
/// Capabilities are declared, not probed. A turn against a model that cannot call tools is refused
/// before a request is spent, which turns a class of silent misbehaviour ("the model ignored the
/// tools") into one clear message.
/// </remarks>
public sealed record ModelDescriptor(
    string Provider,
    string Id,
    string DisplayName,
    int? ContextWindow = null,
    bool SupportsTools = true,
    bool SupportsStreaming = true,
    bool EmitsThinking = false)
{
    /// <summary>Gets the <c>provider:model</c> name used in logs, reports and the CLI.</summary>
    public string QualifiedName => $"{Provider}:{Id}";

    /// <inheritdoc />
    public override string ToString() => QualifiedName;
}
