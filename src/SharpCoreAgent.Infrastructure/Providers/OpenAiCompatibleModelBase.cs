using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Infrastructure.Chat;

namespace SharpCoreAgent.Infrastructure.Providers;

/// <summary>
/// The shared implementation behind every adapter that talks to an OpenAI-compatible server.
/// </summary>
/// <remarks>
/// Provider identity is deliberately absent: no endpoint, key name or catalogue lives here, so a
/// provider that derives from this class declares its own and cannot accidentally inherit another's.
/// Everything protocol-shaped — request mapping, SSE parsing, error classification — is in the
/// transport, which means an adapter is a constructor call and nothing else.
/// </remarks>
public abstract class OpenAiCompatibleModelBase : IStreamingChatModel
{
    private readonly OpenAiCompatibleTransport _transport;

    /// <summary>
    /// Creates an adapter around the shared transport.
    /// </summary>
    /// <param name="http">The client to send on; the caller owns it and its timeout must allow a long stream.</param>
    /// <param name="credentials">The credential store the key is read from at request time.</param>
    /// <param name="model">The descriptor of the model to talk to, taken unchanged.</param>
    /// <param name="endpoint">The absolute <c>/chat/completions</c> URL of the server.</param>
    /// <param name="credentialNames">The configuration keys to try, most specific first.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    protected OpenAiCompatibleModelBase(
        HttpClient http,
        ICredentialStore credentials,
        ModelDescriptor model,
        Uri endpoint,
        IReadOnlyList<string> credentialNames)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);

        _transport = new OpenAiCompatibleTransport(http, model, endpoint, credentialNames, credentials);
    }

    /// <summary>Gets the model this adapter speaks to.</summary>
    public ModelDescriptor Model => _transport.Model;

    /// <summary>Gets the endpoint requests are posted to.</summary>
    public Uri Endpoint => _transport.Endpoint;

    /// <summary>
    /// Describes the request target without its credential.
    /// </summary>
    /// <returns>Method, host and path, for <c>--verbose</c> output.</returns>
    public string DescribeRequest() => _transport.DescribeRequest();

    /// <inheritdoc />
    public IAsyncEnumerable<ChatStreamEvent> StreamAsync(
        ChatRequest request,
        CancellationToken cancellationToken) => _transport.StreamAsync(request, cancellationToken);

    /// <summary>
    /// Sends one request and returns the whole response.
    /// </summary>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">Aborts the request.</param>
    /// <returns>The completed response.</returns>
    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken cancellationToken) =>
        _transport.CompleteAsync(request, cancellationToken);
}
