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
    public static async IAsyncEnumerable<string> ReadDataAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);

        var payload = new StringBuilder(capacity: 512);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
            {
                // A final event without its trailing blank line is still an event.
                if (payload.Length > 0)
                {
                    yield return payload.ToString();
                }

                yield break;
            }

            if (line.Length == 0)
            {
                if (payload.Length > 0)
                {
                    yield return payload.ToString();
                    payload.Clear();
                }

                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
            {
                continue;
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
}
