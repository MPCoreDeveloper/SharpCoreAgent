using System.Globalization;

namespace SharpCoreAgent.Domain.Conversation;

/// <summary>
/// One image a message carries: its media type and its bytes, base64-encoded.
/// </summary>
/// <remarks>
/// The bytes rather than a path, because a path means nothing to a provider and the file can change or
/// vanish between the tool call that read it and the request that carries it. Base64 rather than a
/// <c>byte[]</c>, because that is the only form a wire format asks for and encoding once, where the file
/// is read, keeps a second copy of the same buffer out of the transport.
/// <para>
/// An image travels on a user message only. Every compatible provider rejects an image on the tool role,
/// so the loop attaches what a tool returned to the message that follows its result rather than to the
/// result itself.
/// </para>
/// </remarks>
public sealed record ImageContent
{
    /// <summary>Gets the media type, for example <c>image/png</c>.</summary>
    public required string MediaType { get; init; }

    /// <summary>Gets the bytes, base64-encoded without line breaks.</summary>
    public required string Base64 { get; init; }

    /// <summary>Gets the approximate size of the image before encoding, in bytes.</summary>
    /// <remarks>
    /// Base64 spends four characters on every three bytes, so the pre-encoding size is three quarters of
    /// the string's length; the last group's padding makes it approximate rather than exact.
    /// </remarks>
    public int ByteCount => Base64.Length / 4 * 3;

    /// <summary>Renders the image as the <c>data:</c> URI an OpenAI-compatible provider accepts.</summary>
    /// <returns>A URI of the form <c>data:image/png;base64,AAAA</c>.</returns>
    public string ToDataUri() => $"data:{MediaType};base64,{Base64}";

    /// <inheritdoc />
    /// <remarks>Names the type and the size, never the payload: a base64 image in a log line is unreadable and long.</remarks>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{MediaType} · {ByteCount} bytes");
}
