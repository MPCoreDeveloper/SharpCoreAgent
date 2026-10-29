namespace SharpCoreAgent.Infrastructure.McpServer;

/// <summary>
/// What the server does before a <c>tools/call</c> reaches the tool it names.
/// </summary>
/// <remarks>
/// The session runs one of these for a call and for nothing else: <c>initialize</c>, <c>ping</c> and
/// <c>tools/list</c> answer at handshake speed even while the index is being brought up to date, because a slow
/// handshake reads as a broken server. The session does not know what the gate does — this build hands it the
/// index refresh — which is what keeps the protocol provable without an index and the refresh provable without
/// a socket.
/// </remarks>
public interface IMcpCallGate
{
    /// <summary>
    /// Prepares the server for one call.
    /// </summary>
    /// <param name="cancellationToken">Cancels the preparation; the call it belongs to is cancelled with it.</param>
    /// <returns>A task that completes when the server is as ready as it is going to be.</returns>
    /// <remarks>
    /// A gate must not throw for a condition the caller can still be answered through: a missing, busy or
    /// corrupt index is something the tool's own heading reports, never a reason to turn a readable answer into
    /// an error. Cancellation is the one exception, and it is meant to travel.
    /// </remarks>
    ValueTask PrepareAsync(CancellationToken cancellationToken);
}
