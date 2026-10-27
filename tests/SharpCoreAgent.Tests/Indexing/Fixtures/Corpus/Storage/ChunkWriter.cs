namespace Demo.Storage;

/// Splits content at content-defined boundaries found with a rolling hash, so one edit moves one chunk.
public sealed class ChunkWriter
{
    private const uint BoundaryMask = 0xFFF;

    public IReadOnlyList<(int Length, uint Fingerprint)> Write(ReadOnlySpan<byte> content)
    {
        var chunks = new List<(int Length, uint Fingerprint)>();
        var hash = 0u;
        var start = 0;

        for (var index = 0; index < content.Length; index++)
        {
            hash = unchecked((hash << 1) + content[index]);
            if ((hash & BoundaryMask) != BoundaryMask)
            {
                continue;
            }

            chunks.Add((index - start, hash));
            start = index;
        }

        return chunks;
    }
}
