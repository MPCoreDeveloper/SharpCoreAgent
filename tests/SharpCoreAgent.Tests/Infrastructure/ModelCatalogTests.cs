using SharpCoreAgent.Infrastructure.Providers;
using Xunit;

namespace SharpCoreAgent.Tests.Infrastructure;

/// <summary>
/// What the catalogue promises: the model a turn runs on when none is named, and the identifiers a
/// transcript written by an older build still has to resolve.
/// </summary>
/// <remarks>
/// The second half is the reason the retired entries are still here. Moving a default changes which
/// model a *new* conversation starts on; it must never make an existing transcript unopenable, because
/// `chat --session` takes the conversation's model from the file rather than the command line.
/// </remarks>
public sealed class ModelCatalogTests
{
    [Fact]
    public void TheDefaultsAreTheModelsTheProvidersPublishToday()
    {
        Assert.Equal("deepseek-flash", DeepSeekChatModel.DefaultModelId);
        Assert.Equal("grok-4.7", GrokChatModel.DefaultModelId);
        Assert.Equal(DeepSeekChatModel.DefaultModelId, ModelCatalog.DescriptorFor("deepseek", null).Id);
        Assert.Equal(GrokChatModel.DefaultModelId, ModelCatalog.DescriptorFor("grok", null).Id);
    }

    [Fact]
    public void TheDefaultIsTheFirstModelEachProviderLists()
    {
        // `models` prints the catalogue in this order, so the model a run would pick is the one the
        // developer reads first.
        Assert.Equal(DeepSeekChatModel.DefaultModelId, ModelCatalog.ForProvider("deepseek")[0].Id);
        Assert.Equal(GrokChatModel.DefaultModelId, ModelCatalog.ForProvider("grok")[0].Id);
    }

    [Fact]
    public void RetiredIdentifiersStillResolveSoOldTranscriptsOpen()
    {
        string[] retired =
        [
            "deepseek:deepseek-chat",
            "deepseek:deepseek-reasoner",
            "grok:grok-4",
            "grok:grok-3",
            "grok:grok-3-mini",
        ];

        Assert.All(retired, name =>
        {
            var descriptor = ModelCatalog.TryParseQualified(name, out var parsed) ? parsed! : null;
            Assert.NotNull(descriptor);
            Assert.Equal(name, descriptor.QualifiedName);
            Assert.Contains(ModelCatalog.All, model => model.QualifiedName == name);
        });
    }

    [Fact]
    public void TheCataloguesListTheModelsTheProvidersAdvertisedOnTheLiveCheck()
    {
        // Checked against GET /models on 2026-11-04. The retired aliases after the first two entries are kept
        // deliberately so old transcripts still resolve; a new live model moves this list in a reviewable diff.
        Assert.Equal(
            [
                "deepseek-flash",
                "deepseek-v4-pro",
                "deepseek-chat",
                "deepseek-reasoner",
            ],
            DeepSeekChatModel.Catalog.Select(model => model.Id));

        Assert.Equal(
            [
                "grok-4.7",
                "grok-4.6",
                "grok-4.5",
                "grok-4.3",
                "grok-4.20-0309-non-reasoning",
                "grok-4.20-0309-reasoning",
                "grok-4.20-multi-agent-0309",
                "grok-build-0.1",
                "grok-4",
                "grok-3",
                "grok-3-mini",
            ],
            GrokChatModel.Catalog.Select(model => model.Id));
    }

    [Fact]
    public void TheLiveContextWindowsArePinned()
    {
        Assert.Equal(1_048_576, DeepSeekChatModel.DescriptorFor("deepseek-flash").ContextWindow);
        Assert.Equal(1_048_576, DeepSeekChatModel.DescriptorFor("deepseek-v4-pro").ContextWindow);
        Assert.Equal(500_000, GrokChatModel.DescriptorFor("grok-4.6").ContextWindow);
        Assert.Equal(1_000_000, GrokChatModel.DescriptorFor("grok-4.3").ContextWindow);
        Assert.Equal(1_000_000, GrokChatModel.DescriptorFor("grok-4.20-0309-reasoning").ContextWindow);
        Assert.Equal(256_000, GrokChatModel.DescriptorFor("grok-build-0.1").ContextWindow);
    }

    [Fact]
    public void OnlyTheModelsDeclaredToReadImagesSaySo()
    {
        // `view_image` asks the session's model this question before it opens a file, so the flag is a promise
        // made to a tool rather than a line of advertising: an entry that says yes must be one whose provider
        // accepts an `image_url` part, and a text-only entry has to refuse the picture rather than let the model
        // invent a description of one it never saw.
        Assert.True(DeepSeekChatModel.DescriptorFor("deepseek-flash").SupportsImages);
        Assert.False(DeepSeekChatModel.DescriptorFor("deepseek-v4-pro").SupportsImages);
        Assert.False(DeepSeekChatModel.DescriptorFor("deepseek-chat").SupportsImages);
        Assert.False(DeepSeekChatModel.DescriptorFor("deepseek-reasoner").SupportsImages);
        Assert.All(DemoChatModel.Catalog, model => Assert.False(model.SupportsImages));

        Assert.All(
            GrokChatModel.Catalog.Where(model => !string.Equals(model.Id, "grok-3-mini", StringComparison.Ordinal)),
            model => Assert.True(model.SupportsImages));
        Assert.False(GrokChatModel.DescriptorFor("grok-3-mini").SupportsImages);

        // A compatible endpoint is named by the developer and its protocol defines the part, so the build
        // promises it for every identifier rather than refusing a server that really does read pictures.
        Assert.True(OpenAiCompatibleChatModel.DescriptorFor("some-vision-model").SupportsImages);
    }

    [Fact]
    public void AnIdentifierTheCatalogueDoesNotListStillSynthesisesADescriptor()
    {
        // A model released after this build must not be a hard failure: the provider is the authority
        // on what it serves, and a transcript naming one has to remain readable.
        var descriptor = ModelCatalog.DescriptorFor("grok", "grok-5-unreleased");

        Assert.Equal("grok:grok-5-unreleased", descriptor.QualifiedName);
        Assert.True(descriptor.SupportsTools);
    }

    [Fact]
    public void NoProviderListsAModelTwice()
    {
        Assert.All(ModelCatalog.Providers, provider =>
        {
            var names = ModelCatalog.ForProvider(provider).Select(model => model.Id).ToList();
            Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        });
    }
}
