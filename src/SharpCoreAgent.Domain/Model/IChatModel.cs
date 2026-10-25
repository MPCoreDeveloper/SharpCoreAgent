namespace SharpCoreAgent.Domain.Model;

/// <summary>
/// A model that answers a request in one piece.
/// </summary>
/// <remarks>
/// Non-streaming is expressed as a port of its own rather than a flag so callers cannot accidentally
/// depend on a buffered response where the CLI needs to paint text as it arrives.
/// </remarks>
public interface IChatModel
{
    /// <summary>Gets the model this instance speaks to.</summary>
    ModelDescriptor Model { get; }

    /// <summary>
    /// Sends one request and returns the whole response.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The completed response.</returns>
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A model that answers a request as a stream of increments.
/// </summary>
/// <remarks>
/// The stream must end with <see cref="StreamCompleted"/>; ending without it is a protocol error the
/// agent reports as a failed turn. Implementations must not retry after yielding a delta -- the
/// retry decorator owns that policy, and it can only honour it if the adapter is honest about when
/// the first delta left.
/// </remarks>
public interface IStreamingChatModel
{
    /// <summary>Gets the model this instance speaks to.</summary>
    ModelDescriptor Model { get; }

    /// <summary>
    /// Sends one request and streams the response.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The increments, ending with <see cref="StreamCompleted"/>.</returns>
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Turns a descriptor into a live model, applying whatever decorators the deployment needs.
/// </summary>
public interface IChatModelResolver
{
    /// <summary>
    /// Resolves the adapter for a descriptor.
    /// </summary>
    /// <param name="model">The descriptor, normally taken from the catalog or a session.</param>
    /// <returns>A streaming model, already wrapped in retry, ready to use.</returns>
    /// <exception cref="Errors.ModelCredentialException">The provider's credential is not configured.</exception>
    /// <exception cref="ArgumentException">No adapter serves this provider.</exception>
    IStreamingChatModel Resolve(ModelDescriptor model);
}
