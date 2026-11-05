using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// The JSON-RPC conversation a connection speaks, independent of whether it is carried by a child process or HTTP.
/// </summary>
/// <remarks>
/// The stdio transport is <see cref="JsonRpcPipe"/>; the remote transport is <see cref="HttpJsonRpcChannel"/>. Keeping
/// this small seam means handshake, tool discovery, supervision and the adapters do not know which one is underneath.
/// </remarks>
internal interface IJsonRpcChannel : IAsyncDisposable
{
    /// <summary>Gets a value indicating whether the channel has stopped accepting requests.</summary>
    bool IsClosed { get; }

    /// <summary>Gets or sets the handler for a request the server sends to this client.</summary>
    Func<string, JsonElement, CancellationToken, Task<JsonObject?>>? ServerRequestHandler { get; set; }

    /// <summary>Sends one request and waits for its answer.</summary>
    /// <param name="method">The JSON-RPC method.</param>
    /// <param name="parameters">The parameters object, or null.</param>
    /// <param name="timeout">The wall-clock ceiling for this request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The result element.</returns>
    Task<JsonElement> RequestAsync(
        string method,
        JsonObject? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>Sends one notification, which has no answer.</summary>
    /// <param name="method">The JSON-RPC method.</param>
    /// <param name="parameters">The parameters object, or null.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The write.</returns>
    Task NotifyAsync(string method, JsonObject? parameters, CancellationToken cancellationToken);
}
