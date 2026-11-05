namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// A server the developer named could not be brought up.
/// </summary>
/// <remarks>
/// Startup is atomic by design: the message a turn runs in either has the tools the developer granted or
/// the process never starts. That is why this is an exception with the command line in it rather than a
/// tool result — there is no turn yet to read a result, and a session silently missing three of the five
/// tools that were asked for is the failure nobody can debug afterwards.
/// </remarks>
public sealed class McpStartupException : Exception
{
    /// <summary>
    /// Creates the failure.
    /// </summary>
    /// <param name="message">What went wrong, naming the command line and the reason.</param>
    public McpStartupException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates the failure with the exception that caused it.
    /// </summary>
    /// <param name="message">What went wrong, naming the command line and the reason.</param>
    /// <param name="innerException">The underlying failure, when there is one.</param>
    public McpStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The conversation with one server stopped working.
/// </summary>
/// <remarks>
/// Raised inside the transport and turned into a tool result by <see cref="McpTool"/>, so a server that
/// crashed costs one call rather than the session. It never escapes as an exception to the agent loop.
/// </remarks>
public sealed class McpTransportException : Exception
{
    /// <summary>
    /// Creates the failure.
    /// </summary>
    /// <param name="message">Why the conversation stopped, phrased for a developer reading a tool result.</param>
    public McpTransportException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates the failure with the exception that caused it.
    /// </summary>
    /// <param name="message">Why the conversation stopped.</param>
    /// <param name="innerException">The underlying failure, when there is one.</param>
    public McpTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A sampling request the client refuses, or cannot answer.
/// </summary>
/// <remarks>
/// Raised while answering a server request, then converted by <see cref="JsonRpcPipe"/> into a JSON-RPC error. It is
/// not a tool failure: the server asked the <em>client</em> to do something, and the answer belongs on the server
/// request's id rather than in a tool result.
/// </remarks>
public sealed class McpSamplingException : Exception
{
    /// <summary>
    /// Creates the refusal.
    /// </summary>
    /// <param name="message">Why the request was refused.</param>
    public McpSamplingException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A server answered a request with a JSON-RPC error object.
/// </summary>
public sealed class JsonRpcException : Exception
{
    /// <summary>
    /// Creates the failure from the server's error object.
    /// </summary>
    /// <param name="code">The JSON-RPC error code the server reported.</param>
    /// <param name="message">The server's own message.</param>
    public JsonRpcException(int code, string message)
        : base(message) => Code = code;

    /// <summary>Gets the error code the server reported.</summary>
    public int Code { get; }
}
