using SharpCoreAgent.Domain.Sessions;

namespace SharpCoreAgent.Infrastructure.Providers;

/// <summary>
/// One model's line in a cost report: the tokens it reported, the price the build knows for it, and
/// what those tokens cost.
/// </summary>
/// <param name="Usage">The tokens the session accumulated for this model.</param>
/// <param name="Price">The published price, or <see langword="null"/> when the build knows none.</param>
public sealed record CostLine(ModelUsage Usage, ModelPrice? Price)
{
    /// <summary>Gets the <c>provider:model</c> name of the line.</summary>
    public string ModelId => Usage.ModelId;

    /// <summary>Gets a value indicating whether the build knows what this model costs.</summary>
    public bool IsPriced => Price is not null;

    /// <summary>
    /// Gets what the reported tokens cost, or <see langword="null"/> when the price or the usage is
    /// unknown.
    /// </summary>
    public decimal? Cost => Price?.Cost(Usage.InputTokens, Usage.OutputTokens);
}

/// <summary>
/// What the tokens a session reported would cost at the prices this build knows.
/// </summary>
/// <param name="Lines">One line per model that served the session, ordered by name.</param>
/// <param name="Total">
/// The sum of the lines whose cost is known. Zero when nothing could be priced, which is why
/// <see cref="IsComplete"/> is printed beside it.
/// </param>
/// <param name="Unpriced">How many lines have no published price.</param>
/// <param name="UnknownUsage">How many lines are priced but reported no tokens.</param>
/// <remarks>
/// The two counts exist separately because they are different problems with different fixes: one is a
/// gap in the table, the other is a provider that said nothing. Collapsing them into a single
/// "incomplete" flag would hide which one to go and fix.
/// </remarks>
public sealed record CostReport(IReadOnlyList<CostLine> Lines, decimal Total, int Unpriced, int UnknownUsage)
{
    /// <summary>
    /// Gets how many lines have a cost: an amount per line, and a price behind it.
    /// </summary>
    /// <remarks>
    /// The count that decides whether <see cref="Total"/> may be printed at all. A total of zero drawn
    /// from zero measured lines is not a measurement, and a report that shows <c>$0.00</c> for a
    /// session nobody could price is the one lie this feature exists to avoid.
    /// </remarks>
    public int Measured => Lines.Count - Unpriced - UnknownUsage;

    /// <summary>
    /// Gets a value indicating whether every line was priced and measured, so <see cref="Total"/> is a
    /// measurement rather than a floor.
    /// </summary>
    public bool IsComplete => Unpriced == 0 && UnknownUsage == 0;

    /// <summary>
    /// Builds a report from a session's per-model usage.
    /// </summary>
    /// <param name="usage">The rows <c>AgentSession.UsageByModel</c> exposes.</param>
    /// <returns>The report; empty, with a zero total, when the session recorded nothing.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="usage"/> is null.</exception>
    public static CostReport Create(IReadOnlyList<ModelUsage> usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        var lines = new List<CostLine>(usage.Count);
        var total = 0m;
        var unpriced = 0;
        var unknownUsage = 0;

        foreach (var row in usage.OrderBy(entry => entry.ModelId, StringComparer.Ordinal))
        {
            var line = new CostLine(row, ModelPricing.Find(row.ModelId));
            lines.Add(line);

            if (!line.IsPriced)
            {
                unpriced++;
                continue;
            }

            if (line.Cost is not { } cost)
            {
                unknownUsage++;
                continue;
            }

            total += cost;
        }

        return new CostReport(lines, total, unpriced, unknownUsage);
    }
}
