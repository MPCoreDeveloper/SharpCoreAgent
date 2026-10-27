using SharpCoreAgent.Domain.Index;

namespace SharpCoreAgent.Indexing.Retrieval;

/// <summary>
/// Turns a declaration into the passage a citation can quote.
/// </summary>
/// <remarks>
/// The graph legs know which declaration answered the question, but a model needs text it can read and cite, and
/// that text lives in exactly one passage. The passage that starts at the declaration is the right one; when the
/// index has none — a member inside an oversized type, say — the narrowest passage that contains its first line is
/// the best available answer.
/// </remarks>
internal static class EvidenceBlocks
{
    /// <summary>
    /// Builds the block for one declaration.
    /// </summary>
    /// <param name="snapshot">The build the declaration belongs to.</param>
    /// <param name="row">The declaration to quote.</param>
    /// <param name="relation">How the block was reached, or <see langword="null"/> when only its text matched.</param>
    /// <param name="score">The relevance score to report.</param>
    /// <returns>The block, or <see langword="null"/> when no passage in the declaring file covers it.</returns>
    public static CodeEvidence? ForSymbol(
        IndexSnapshot snapshot,
        SymbolRow row,
        string? relation,
        double score)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(row);

        ChunkRow? chosen = null;

        foreach (var chunk in snapshot.Chunks.Values)
        {
            if (chunk.FileId != row.FileId)
            {
                continue;
            }

            if (string.Equals(chunk.SymbolKey, row.Key, StringComparison.Ordinal))
            {
                chosen = chunk;
                break;
            }

            if (chunk.StartLine <= row.StartLine &&
                row.StartLine <= chunk.EndLine &&
                (chosen is null || chunk.EndLine - chunk.StartLine < chosen.EndLine - chosen.StartLine))
            {
                chosen = chunk;
            }
        }

        if (chosen is null)
        {
            return null;
        }

        return new CodeEvidence
        {
            Path = chosen.Path,
            StartLine = chosen.StartLine,
            EndLine = chosen.EndLine,
            Sha8 = chosen.Sha8,
            Text = chosen.Text,
            Kind = chosen.Kind,
            Symbol = row.QualifiedName,
            Score = score,
            Legs = [EvidenceLeg.Graph],
            IsStale = snapshot.Files.TryGetValue(row.FileId, out var file) && file.IsStale,
            Relation = relation,
        };
    }
}
