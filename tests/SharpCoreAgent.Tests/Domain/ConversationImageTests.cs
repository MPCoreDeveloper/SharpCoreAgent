using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using Xunit;

namespace SharpCoreAgent.Tests.Domain;

/// <summary>
/// An image on the conversation model: a user message may carry one beside its text, it renders as the
/// <c>data:</c> URI a provider accepts, and a tool result can hand one to the loop.
/// </summary>
/// <remarks>
/// The size rule is the subtle one. <see cref="ImageContent.ByteCount"/> is derived from the base64 length, so a
/// test that pins it also pins the "four characters per three bytes" arithmetic every log line and every
/// tool-result note depends on.
/// </remarks>
public sealed class ConversationImageTests
{
    private static ImageContent Png => new() { MediaType = "image/png", Base64 = "iVBORw0KGgo=" };

    [Fact]
    public void AUserMessageCarriesTheImagesItWasGiven()
    {
        var image = Png;

        var message = ChatMessage.User("what is this?", [image]);

        Assert.Equal(ChatRole.User, message.Role);
        Assert.Equal("what is this?", message.Content);
        Assert.True(message.HasImages);
        Assert.Same(image, Assert.Single(message.Images));
        Assert.Empty(message.ToolCalls);
    }

    [Fact]
    public void AUserMessageWithoutImagesHasNone()
    {
        Assert.False(ChatMessage.User("plain").HasImages);
        Assert.False(ChatMessage.User("plain", null).HasImages);
        Assert.False(ChatMessage.User("plain", []).HasImages);
    }

    [Fact]
    public void AnImageRendersAsTheDataUriAProviderAccepts()
    {
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=", Png.ToDataUri());
    }

    [Fact]
    public void AnImageReportsItsSizeAndNeverItsPayload()
    {
        var image = Png;

        Assert.Equal(9, image.ByteCount);
        Assert.Equal("image/png · 9 bytes", image.ToString());
        Assert.DoesNotContain(image.Base64, image.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AToolResultCanHandAnImageToTheLoop()
    {
        var image = Png;

        var result = ToolResult.Image("call_1", "view_image", "attached", image);

        Assert.False(result.IsError);
        Assert.Equal("attached", result.Content);
        Assert.Same(image, Assert.Single(result.Images));
        Assert.Empty(result.Effects);
    }
}
