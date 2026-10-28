using SharpCoreAgent.Domain.Model;

namespace SharpCoreAgent.Infrastructure.Providers;

/// <summary>
/// The published prices this build knows, and the date they were read.
/// </summary>
/// <remarks>
/// <para>
/// Two rules shape this table. A model it does not list has no price — the report says "no price
/// known" rather than interpolating from a neighbour — and every entry names the page it came from,
/// because the alternative is a number in a source file that nobody can audit.
/// </para>
/// <para>
/// DeepSeek publishes an off-peak and a peak tier, with peak at exactly double. Usage reports say
/// neither which tier a request ran in nor whether its prompt hit the cache, so the table holds the
/// peak cache-miss rate: the report is then the ceiling of what a turn could have cost, never less
/// than the invoice.
/// </para>
/// </remarks>
public static class ModelPricing
{
    /// <summary>Gets the currency every entry is denominated in. <c>USD</c>.</summary>
    /// <remarks>
    /// Printed with every report: a bare <c>$</c> would be an assumption, and this product is used in
    /// countries where it means something else.
    /// </remarks>
    public const string Currency = "USD";

    /// <summary>Gets the date the figures below were read from the providers' pages.</summary>
    /// <remarks>
    /// One date for the whole table rather than one per entry, because the table is refreshed in one
    /// sitting and a per-entry date invites the reader to trust a mix of vintages.
    /// </remarks>
    public static DateOnly AsOf { get; } = new(2026, 10, 27);

    /// <summary>Gets every model this build can price, in display order.</summary>
    public static IReadOnlyList<ModelPrice> All { get; } =
    [
        new(DeepSeekChatModel.ProviderKey, "deepseek-flash", 0.30m, 1.20m, DeepSeekPage),
        new(DeepSeekChatModel.ProviderKey, "deepseek-v4-pro", 1.32m, 3.96m, DeepSeekPage),
        new(GrokChatModel.ProviderKey, "grok-4.7", 2.00m, 6.00m, GrokPage),
    ];

    private const string DeepSeekPage = "https://api-docs.deepseek.com/quick_start/pricing";

    private const string GrokPage = "https://docs.x.ai/docs/models";

    /// <summary>
    /// Finds the price of a <c>provider:model</c> name.
    /// </summary>
    /// <param name="qualifiedName">The name the session records, for example <c>deepseek:deepseek-flash</c>.</param>
    /// <returns>The price, or <see langword="null"/> when the build cannot price that model.</returns>
    /// <remarks>
    /// The comparison is ordinal. A provider's identifiers are case-sensitive on the wire, so a name
    /// that differs only in case is a different model and is reported as unpriced rather than matched
    /// to a price it may not have.
    /// </remarks>
    public static ModelPrice? Find(string? qualifiedName) =>
        TryGet(qualifiedName, out var price) ? price : null;

    /// <summary>
    /// Determines whether a <c>provider:model</c> name has a published price.
    /// </summary>
    /// <param name="qualifiedName">The name the session records.</param>
    /// <param name="price">The price, when one is known.</param>
    /// <returns><see langword="true"/> when the table prices that model.</returns>
    public static bool TryGet(string? qualifiedName, out ModelPrice price)
    {
        price = null!;
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            return false;
        }

        var separator = qualifiedName.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == qualifiedName.Length - 1)
        {
            return false;
        }

        var provider = qualifiedName[..separator];
        var modelId = qualifiedName[(separator + 1)..];
        var match = All.FirstOrDefault(entry =>
            string.Equals(entry.Provider, provider, StringComparison.Ordinal)
            && string.Equals(entry.ModelId, modelId, StringComparison.Ordinal));

        if (match is null)
        {
            return false;
        }

        price = match;
        return true;
    }

    /// <summary>
    /// Prices one model's usage.
    /// </summary>
    /// <param name="qualifiedName">The <c>provider:model</c> name that served the request.</param>
    /// <param name="usage">The reported tokens.</param>
    /// <returns>
    /// The cost, or <see langword="null"/> when the model is unpriced or either half of the usage is
    /// unknown.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="usage"/> is null.</exception>
    public static decimal? Cost(string? qualifiedName, TokenUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return Find(qualifiedName)?.Cost(usage);
    }
}
