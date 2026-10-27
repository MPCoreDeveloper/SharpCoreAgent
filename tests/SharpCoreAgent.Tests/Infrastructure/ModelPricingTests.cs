using System.Globalization;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Infrastructure.Providers;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The price table, pinned: a number that is not asserted is a number that drifts into a report nobody
/// questions.
/// </summary>
/// <remarks>
/// Two failure modes matter more than the arithmetic here. A price that is silently edited, and a
/// price for a model this build cannot name — the first makes every report wrong, the second makes the
/// table look fuller than it is. Both are one assertion away.
/// </remarks>
public sealed class ModelPricingTests
{
    [Fact]
    public void TheTableCarriesOneDateForEveryFigure() =>
        Assert.Equal(new DateOnly(2026, 10, 27), ModelPricing.AsOf);

    [Fact]
    public void TheCurrencyIsStatedBecauseADollarSignIsNot() =>
        Assert.Equal("USD", ModelPricing.Currency);

    [Fact]
    public void EveryEntryCanBeTracedToThePageItCameFrom() =>
        Assert.All(ModelPricing.All, entry => Assert.StartsWith("https://", entry.Source, StringComparison.Ordinal));

    [Fact]
    public void EveryEntryPricesBothHalves() =>
        Assert.All(ModelPricing.All, entry =>
        {
            Assert.True(entry.InputPerMillion > 0m);
            Assert.True(entry.OutputPerMillion > 0m);
        });

    [Fact]
    public void NoModelIsPricedTwice()
    {
        var names = ModelPricing.All.Select(entry => entry.QualifiedName).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryPricedModelIsOneTheCatalogueCanName()
    {
        // A price for a model nothing can select is dead data that reads as coverage.
        var known = ModelCatalog.All.Select(model => model.QualifiedName).ToHashSet(StringComparer.Ordinal);

        Assert.All(ModelPricing.All, entry => Assert.Contains(entry.QualifiedName, known));
    }

    [Theory]
    [InlineData("deepseek:deepseek-flash", "0.30", "1.20")]
    [InlineData("deepseek:deepseek-v4-pro", "1.32", "3.96")]
    [InlineData("grok:grok-4.7", "2.00", "6.00")]
    public void TheRatesAreTheOnesTheProvidersPublish(string qualifiedName, string input, string output)
    {
        // The rates arrive as text because an InlineData double cannot hold 0.30 as a decimal: the
        // attribute hands the test 0.29999999999999998, and the table would look wrong for a reason
        // that has nothing to do with the table.
        var price = Assert.IsType<ModelPrice>(ModelPricing.Find(qualifiedName));

        Assert.Equal(Money(input), price.InputPerMillion);
        Assert.Equal(Money(output), price.OutputPerMillion);
    }

    [Fact]
    public void AMillionTokensInAndOutCostsTheSumOfTheTwoRates()
    {
        var price = Assert.IsType<ModelPrice>(ModelPricing.Find("deepseek:deepseek-flash"));

        Assert.Equal(1.50m, price.Cost(1_000_000, 1_000_000));
    }

    [Fact]
    public void ASmallSessionKeepsItsPrecision()
    {
        // 1,000 in and 2,000 out on the cheapest model: $0.0003 + $0.0024.
        var price = Assert.IsType<ModelPrice>(ModelPricing.Find("deepseek:deepseek-flash"));

        Assert.Equal(0.0027m, price.Cost(1_000, 2_000));
    }

    [Fact]
    public void NoTokensCostsNothing()
    {
        var price = Assert.IsType<ModelPrice>(ModelPricing.Find("grok:grok-4.7"));

        Assert.Equal(0m, price.Cost(0, 0));
    }

    [Fact]
    public void HalfKnownUsageCostsUnknownRatherThanLess()
    {
        var price = Assert.IsType<ModelPrice>(ModelPricing.Find("grok:grok-4.7"));

        Assert.Null(price.Cost(null, 2_000));
        Assert.Null(price.Cost(1_000, null));
        Assert.Null(price.Cost(TokenUsage.Unknown));
        Assert.Null(ModelPricing.Cost("grok:grok-4.7", TokenUsage.Unknown));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("deepseek")]
    [InlineData("deepseek:")]
    [InlineData(":deepseek-flash")]
    [InlineData("deepseek:deepseek-chat")]
    [InlineData("nobody:nothing")]
    public void AnUnlistedNameHasNoPrice(string? qualifiedName)
    {
        Assert.Null(ModelPricing.Find(qualifiedName));
        Assert.False(ModelPricing.TryGet(qualifiedName, out _));
    }

    [Fact]
    public void LookupsAreOrdinalBecauseProvidersAre()
    {
        Assert.Null(ModelPricing.Find("DeepSeek:deepseek-flash"));
        Assert.Null(ModelPricing.Find("deepseek:DEEPSEEK-FLASH"));
    }

    [Fact]
    public void TheTablePricesWhatTheCurrentCatalogueAdds()
    {
        Assert.NotNull(ModelPricing.Find("deepseek:deepseek-flash"));
        Assert.NotNull(ModelPricing.Find("deepseek:deepseek-v4-pro"));
        Assert.NotNull(ModelPricing.Find("grok:grok-4.7"));

        // The identifiers this build shipped first are still listed but no longer published with a
        // price, so they are honestly unpriced rather than quoted from memory.
        Assert.Null(ModelPricing.Find("deepseek:deepseek-chat"));
        Assert.Null(ModelPricing.Find("grok:grok-4"));
    }

    /// <summary>Reads a published rate from the text form the theory carries.</summary>
    /// <param name="text">The rate, for example <c>0.30</c>.</param>
    /// <returns>The rate as the decimal the table holds.</returns>
    private static decimal Money(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);
}
