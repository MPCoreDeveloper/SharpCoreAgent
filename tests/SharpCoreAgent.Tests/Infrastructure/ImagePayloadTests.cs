using SharpCoreAgent.Infrastructure.Images;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// The one place a foreign <c>image/*</c> becomes an attachable picture: five spellings onto four media types, the size
/// estimate both ceilings share, and the closed list that keeps a type a provider would reject out of a request body.
/// </summary>
public sealed class ImagePayloadTests
{
    [Theory]
    [InlineData("image/png", "image/png")]
    [InlineData("IMAGE/PNG", "image/png")]
    [InlineData("image/jpeg", "image/jpeg")]
    [InlineData("image/jpg", "image/jpeg")]
    [InlineData("image/gif", "image/gif")]
    [InlineData("image/webp", "image/webp")]
    [InlineData("image/png; charset=binary", "image/png")]
    public void EverySpellingAProviderAcceptsBecomesOneOfFourTypes(string claimed, string expected)
    {
        Assert.True(ImagePayload.TryMediaType(claimed, out var mediaType));
        Assert.Equal(expected, mediaType);
    }

    [Theory]
    [InlineData("image/bmp")]
    [InlineData("image/svg+xml")]
    [InlineData("image/tiff")]
    [InlineData("image/avif")]
    [InlineData("text/plain")]
    [InlineData("")]
    [InlineData(null)]
    public void EveryOtherTypeIsRefusedAndTheTableIsClosed(string? claimed)
    {
        // bmp is the interesting one: the artefact sink saves it, because saving is a file for a developer, and this
        // refuses it, because attaching is a payload for a provider. Two tables, two questions.
        Assert.False(ImagePayload.TryMediaType(claimed, out var mediaType));
        Assert.Equal(string.Empty, mediaType);
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/bmp", true)]
    [InlineData("IMAGE/WEBP", true)]
    [InlineData("text/html", false)]
    [InlineData(null, false)]
    public void AnImageIsAnythingUnderTheImageType(string? claimed, bool expected) =>
        Assert.Equal(expected, ImagePayload.IsImage(claimed));

    [Theory]
    [InlineData("", 0L)]
    [InlineData("AAAA", 3L)]
    [InlineData("AAA=", 2L)]
    [InlineData("AA==", 1L)]
    public void TheEstimateIsTheDecodedSizeToWithinThePadding(string base64, long expected) =>
        Assert.Equal(expected, ImagePayload.EstimateBytes(base64));

    [Fact]
    public void APayloadAtTheCeilingIsAcceptedAndOneByteOverIsRefused()
    {
        // Three bytes decoded from four characters: the boundary is tested here rather than with a four-mebibyte
        // string, because the rule is arithmetic and the ceiling it is given is the only variable.
        Assert.True(ImagePayload.TryCreate("image/png", "AAAA", maxBytes: 3, out var image, out var accepted));
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal("AAAA", image.Base64);
        Assert.Equal(string.Empty, accepted);

        Assert.False(ImagePayload.TryCreate("image/png", "AAAA", maxBytes: 2, out _, out var refused));
        Assert.Contains("3 B", refused, StringComparison.Ordinal);
        Assert.Contains("2 B", refused, StringComparison.Ordinal);
        Assert.Contains("ceiling", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalNamesTheTypeItRefusedAndWhatItWouldHaveAccepted()
    {
        Assert.False(ImagePayload.TryCreate("image/bmp", "AAAA", maxBytes: 1_024, out _, out var reason));

        Assert.Contains("image/bmp", reason, StringComparison.Ordinal);
        Assert.Contains(ImagePayload.AcceptedList, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void APartWithNoDataIsRefusedBeforeAnyCeilingIsConsulted()
    {
        Assert.False(ImagePayload.TryCreate("image/png", null, maxBytes: 1_024, out _, out var missing));
        Assert.Contains("no data", missing, StringComparison.Ordinal);

        Assert.False(ImagePayload.TryCreate("image/png", "  ", maxBytes: 1_024, out _, out var blank));
        Assert.Contains("no data", blank, StringComparison.Ordinal);
    }
}
