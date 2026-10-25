using System.Runtime.CompilerServices;
using System.Text;

namespace SharpCoreAgent.Infrastructure.Chat;

/// <summary>
/// Splits a <c>text/event-stream</c> body into the payloads of its <c>data:</c> lines.
/// </summary>
/// <remarks>
/// The reader is line-oriented and buffers only the part of an event that has not arrived yet, which
/// is what lets a frame split across TCP chunks be reassembled without waiting for the whole
/// response. Comment lines (started with <c>:</c>, used as keep-alives), <c>event:</c>, <c>id:</c> and
/// <c>retry:</c> lines are skipped: nothing in the OpenAI-compatible protocol needs them.
/// <para>
/// Payloads are yielded as raw strings, including the terminal <c>[DONE]</c> sentinel. Deciding what a
/// payload means is the transport's job, not the reader's.
/// </para>
/// </remarks>
public static class SseReader
{
    private const string DataPrefix = "data:";

    /// <summary>
    /// Reads payloads until the stream ends.
    /// </summary>
    /// <param name="stream">The response body.</param>
    /// <param name="cancellationToken">Aborts the read; the stream is left open, the caller owns it.</param>
    /// <returns>The payload of each event, in order, without the <c>data:</c> prefix.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="IOException">The connection failed mid-stream.</exception>
    public static IAsyncEnumerable<string> ReadDataAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return ReadDataCoreAsync(stream, cancellationToken);
    }

    /// <summary>
    /// Reads payloads until the stream ends.
    /// </summary>
    /// <param name="stream">The response body, already checked for null.</param>
    /// <param name="cancellationToken">Aborts the read.</param>
    /// <returns>The payload of each event, in order.</returns>
    /// <remarks>
    /// The check lives in <see cref="ReadDataAsync"/> on purpose: an iterator method does not run a
    /// single line, not even an argument check, until the caller asks for the first item.
    /// </remarks>
    private static async IAsyncEnumerable<string> ReadDataCoreAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);

        var payload = new StringBuilder(capacity: 512);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
        {
            // A blank line ends the event; anything else may or may not belong to the payload.
            if (line.Length > 0)
            {
                AppendDataLine(payload, line);
                continue;
            }

            if (payload.Length > 0)
            {
                var complete = payload.ToString();
                payload.Clear();
                yield return complete;
            }
        }

        // A final event without its trailing blank line is still an event.
        if (payload.Length > 0)
        {
            yield return payload.ToString();
        }
    }

    /// <summary>
    /// Adds one line to the event being assembled, ignoring the lines the protocol does not need.
    /// </summary>
    /// <param name="payload">The event under construction.</param>
    /// <param name="line">A non-empty line of the stream.</param>
    private static void AppendDataLine(StringBuilder payload, string line)
    {
        // A leading colon is a comment, which is what providers send as a keep-alive.
        if (line[0] == ':')
        {
            return;
        }

        if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
        {
            // event:, id: and retry: carry nothing this client uses.
            return;
        }

        // "data: value" is the documented form; "data:value" occurs in the wild.
        var value = line.AsSpan(DataPrefix.Length);
        if (!value.IsEmpty && value[0] == ' ')
        {
            value = value[1..];
        }

        // Multiple data lines in one event are joined with a newline, per the SSE grammar.
        if (payload.Length > 0)
        {
            payload.Append('\n');
        }

        payload.Append(value);
    }
}
