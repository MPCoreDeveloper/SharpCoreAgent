using System.Globalization;
using SharpCoreAgent.Domain.Index;

namespace SharpCoreAgent.Indexing.Ranking;

/// <summary>
/// What the budget kept, and why it kept so little.
/// </summary>
/// <remarks>
/// The counts are separate on purpose: "two per file" and "twelve in total" are different reasons for a caller to
/// widen the search, and a single dropped count would hide which one applied. A tool that renders <see cref="Note"/>
/// tells the model that its answer was cut, which is the difference between evidence and a misleadingly short answer.
/// </remarks>
/// <param name="Blocks">The blocks that survived, still in rank order.</param>
/// <param name="Considered">How many candidates the ranker offered.</param>
/// <param name="DroppedByFileCap">How many were dropped because their file already had
/// <see cref="EvidenceBudget.MaxBlocksPerFile"/> blocks.</param>
/// <param name="DroppedByCount">How many were dropped because the result already held
/// <see cref="EvidenceBudget.MaxBlocks"/> blocks.</param>
/// <param name="DroppedBySize">How many were dropped because they would have exceeded the caller's character
/// budget.</param>
/// <param name="Trimmed">How many blocks were shortened to <see cref="EvidenceBudget.MaxExcerptChars"/>.</param>
internal sealed record BudgetedEvidence(
    IReadOnlyList<CodeEvidence> Blocks,
    int Considered,
    int DroppedByFileCap,
    int DroppedByCount,
    int DroppedBySize,
    int Trimmed)
{
    /// <summary>Gets how many candidates were left out, for any reason.</summary>
    public int Dropped => DroppedByFileCap + DroppedByCount + DroppedBySize;

    /// <summary>
    /// Gets the sentence a header shows when the answer was cut, or <see langword="null"/> when it was not.
    /// </summary>
    public string? Note => Dropped == 0
        ? null
        : string.Create(
            CultureInfo.InvariantCulture,
            $"{Dropped} of {Considered} candidates left out to stay inside the budget");
}


/// <summary>
/// The selection budget: how many blocks, from how many files, at what length, inside how many characters.
/// </summary>
/// <remarks>
/// The caps are not about politeness, they are about the shape of a useful answer. Five passages from one 300-line
/// file tell a model less than one passage from each of five files, and an answer that silently spends the whole
/// tool-result allowance on the file that happens to repeat the query word is worse than a short answer that says it
/// was short.
/// <para>
/// <see cref="MaxExcerptChars"/> is the per-block cap and <c>maxChars</c> is the whole-result cap. The second is
/// measured with an explicit estimate rather than by rendering the tool output here, because rendering belongs to the
/// tool and the budget must not depend on its formatting.
/// </para>
/// </remarks>
internal static class EvidenceBudget
{
    /// <summary>The most blocks one answer may hold, which is <see cref="CodeQuery.MaxK"/>.</summary>
    internal const int MaxBlocks = CodeQuery.MaxK;

    /// <summary>The most blocks one file may contribute.</summary>
    internal const int MaxBlocksPerFile = 2;

    /// <summary>The most characters one excerpt may hold.</summary>
    internal const int MaxExcerptChars = 1600;

    /// <summary>The budget a caller uses when it has no <c>MaxToolResultChars</c> of its own.</summary>
    internal const int DefaultMaxChars = 24_000;

    /// <summary>
    /// What one block costs beyond its excerpt: the heading, the line numbers and the separators.
    /// </summary>
    /// <remarks>
    /// Deliberately generous. Underestimating would let the rendered result exceed the caller's allowance after the
    /// budget claimed it fit, which is the one thing this type must not do.
    /// </remarks>
    internal const int OverheadChars = 96;

    /// <summary>
    /// Applies the budget to a ranked list.
    /// </summary>
    /// <param name="ranked">The blocks, best first.</param>
    /// <param name="maxChars">The character allowance for the whole answer.</param>
    /// <returns>The blocks that fit, plus the counts that explain the rest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ranked"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxChars"/> is not positive.</exception>
    public static BudgetedEvidence Apply(IReadOnlyList<CodeEvidence> ranked, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

        var blocks = new List<CodeEvidence>(Math.Min(ranked.Count, MaxBlocks));
        var perFile = new Dictionary<string, int>(StringComparer.Ordinal);

        var used = 0;
        var byFile = 0;
        var byCount = 0;
        var bySize = 0;
        var trimmed = 0;

        foreach (var block in ranked)
        {
            if (blocks.Count >= MaxBlocks)
            {
                byCount++;
                continue;
            }

            var seen = perFile.GetValueOrDefault(block.Path);
            if (seen >= MaxBlocksPerFile)
            {
                byFile++;
                continue;
            }

            var (text, wasTrimmed) = Excerpt(block.Text);
            var cost = Cost(block, text);

            // A block that does not fit is skipped rather than ending the loop: the next candidate may be a short one
            // that still helps, and stopping would throw away evidence for the sake of the block before it.
            if (used + cost > maxChars)
            {
                bySize++;
                continue;
            }

            blocks.Add(wasTrimmed ? block with { Text = text } : block);
            if (wasTrimmed)
            {
                trimmed++;
            }

            perFile[block.Path] = seen + 1;
            used += cost;
        }

        return new BudgetedEvidence(blocks, ranked.Count, byFile, byCount, bySize, trimmed);
    }

    /// <summary>
    /// Shortens one excerpt to <see cref="MaxExcerptChars"/>, on a line boundary when there is one.
    /// </summary>
    /// <param name="text">The passage text.</param>
    /// <returns>The text to return, and whether it was cut.</returns>
    private static (string Text, bool Trimmed) Excerpt(string text)
    {
        if (text.Length <= MaxExcerptChars)
        {
            return (text, false);
        }

        var cut = text.LastIndexOf('\n', MaxExcerptChars - 1);
        if (cut <= 0)
        {
            // One very long line: there is no boundary to cut on, so the cap decides and the excerpt ends mid-line
            // rather than not at all.
            cut = MaxExcerptChars;
        }

        return (text[..cut].TrimEnd(), true);
    }

    /// <summary>
    /// Estimates what one rendered block costs.
    /// </summary>
    /// <param name="block">The block.</param>
    /// <param name="text">The excerpt that will be rendered.</param>
    /// <returns>The character estimate.</returns>
    private static int Cost(CodeEvidence block, string text) =>
        OverheadChars + block.Path.Length + (block.Symbol?.Length ?? 0) + text.Length;
}
