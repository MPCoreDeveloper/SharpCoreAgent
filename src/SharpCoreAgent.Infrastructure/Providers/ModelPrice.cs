using SharpCoreAgent.Domain.Model;

namespace SharpCoreAgent.Infrastructure.Providers;

/// <summary>
/// What one model costs, per million tokens, in the currency <see cref="ModelPricing.Currency"/> names.
/// </summary>
/// <param name="Provider">The provider key: <c>deepseek</c>, <c>grok</c>, and so on.</param>
/// <param name="ModelId">The provider's model identifier, as sent on the wire.</param>
/// <param name="InputPerMillion">The price of one million input tokens.</param>
/// <param name="OutputPerMillion">The price of one million output tokens.</param>
/// <param name="Source">The page the figures were read from, so a stale number can be traced.</param>
/// <remarks>
/// Prices are vendor facts and move on the vendor's schedule, which is why every entry carries the
/// page it came from and the table carries the date it was read: a report that says <c>$0.02</c> is
/// only useful if it also says how old the number behind it is.
/// </remarks>
public sealed record ModelPrice(
    string Provider,
    string ModelId,
    decimal InputPerMillion,
    decimal OutputPerMillion,
    string Source)
{
    /// <summary>Gets the <c>provider:model</c> name this price belongs to.</summary>
    public string QualifiedName => $"{Provider}:{ModelId}";

    /// <summary>
    /// Prices a request's or a turn's tokens.
    /// </summary>
    /// <param name="inputTokens">The reported input tokens.</param>
    /// <param name="outputTokens">The reported output tokens.</param>
    /// <returns>
    /// The cost, or <see langword="null"/> when either half is unknown — a half-known usage priced
    /// against a published rate would report less than the turn cost.
    /// </returns>
    /// <remarks>
    /// <see cref="decimal"/> rather than <see cref="double"/>: the cheapest rate this table holds is
    /// three tenths of a cent per million tokens, and a binary fraction would not reproduce the
    /// product a person checks on paper.
    /// </remarks>
    public decimal? Cost(int? inputTokens, int? outputTokens) =>
        inputTokens is null || outputTokens is null
            ? null
            : ((inputTokens.Value * InputPerMillion) + (outputTokens.Value * OutputPerMillion)) / 1_000_000m;

    /// <summary>
    /// Prices a usage report.
    /// </summary>
    /// <param name="usage">The usage to price.</param>
    /// <returns>The cost, or <see langword="null"/> when either half of the usage is unknown.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="usage"/> is null.</exception>
    public decimal? Cost(TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return Cost(usage.InputTokens, usage.OutputTokens);
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"{QualifiedName} {InputPerMillion}/{OutputPerMillion} per 1M {ModelPricing.Currency}");
}
