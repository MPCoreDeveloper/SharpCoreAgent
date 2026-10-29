using System.Text;
using System.Text.Json.Nodes;
using SharpCoreAgent.Infrastructure.Mcp;

namespace SharpCoreAgent.Infrastructure.McpServer;

/// <summary>
/// Writes protocol messages to the client's stdin, one JSON object per line and nothing else.
/// </summary>
/// <remarks>
/// The mirror of the write half of <see cref="JsonRpcPipe"/>, with one rule the client does not need: only
/// serialized messages reach the stream, never a string a caller assembled. `ToJsonString` escapes control
/// characters, so a tool result full of newlines — which every evidence answer is — still travels as exactly
/// one line, and there is no code path here that could split a frame.
/// <para>
/// The encoding is UTF-8 without a byte-order mark, deliberately: a BOM in the middle of a conversation is
/// the first byte of the next message, and no client would have a reason to tolerate it.
/// </para>
/// </remarks>
internal sealed class StdioRpcWriter : IAsyncDisposable
{
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    /// <summary>
    /// Creates a writer over an already-open stream.
    /// </summary>
    /// <param name="output">Where messages are written; the process's stdout in production.</param>
    public StdioRpcWriter(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <summary>
    /// Writes one message as one line.
    /// </summary>
    /// <param name="message">The complete JSON-RPC message.</param>
    /// <param name="cancellationToken">Cancels the write, which only happens if the client stopped reading.</param>
    /// <returns>A task that completes once the line has been flushed.</returns>
    /// <exception cref="McpTransportException">The client's input is gone.</exception>
    public async ValueTask WriteAsync(JsonNode message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            throw new McpTransportException($"the client's input could not be written: {ex.Message}", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Releases the write gate. The stream itself belongs to the process, so it stays open.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
