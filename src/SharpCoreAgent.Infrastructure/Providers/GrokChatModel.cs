using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;

namespace SharpCoreAgent.Infrastructure.Providers;

/// <summary>
/// The xAI Grok adapter: the same protocol and transport as DeepSeek, a different address and catalogue.
/// </summary>
/// <remarks>
/// Existing at all is the point. The second provider is what proves the model port is not shaped around
/// one vendor's quirks; because everything protocol-specific lives in the transport, this file is only
/// an address, a key name and a list.
/// </remarks>
public sealed class GrokChatModel : OpenAiCompatibleModelBase
{
    /// <summary>Creates the adapter for a catalogue model.</summary>
    /// <param name="http">The client to send on; the caller owns it.</param>
    /// <param name="credentials">The credential store the key is read from at request time.</param>
    /// <param name="modelId">The model identifier; <see cref="DefaultModelId"/> when null or blank.</param>
    public GrokChatModel(HttpClient http, ICredentialStore credentials, string? modelId = null)
        : this(http, credentials, DescriptorFor(modelId))
    {
    }

    /// <summary>Creates the adapter for a descriptor the caller already resolved.</summary>
    /// <param name="http">The client to send on; the caller owns it.</param>
    /// <param name="credentials">The credential store the key is read from at request time.</param>
    /// <param name="model">The descriptor to speak for, taken unchanged.</param>
    public GrokChatModel(HttpClient http, ICredentialStore credentials, ModelDescriptor model)
        : base(http, credentials, model, ChatEndpoint, CredentialNames)
    {
    }

    /// <summary>Gets the provider key. <c>grok</c>.</summary>
    public const string ProviderKey = "grok";

    /// <summary>Gets the model used when the caller names none.</summary>
    public const string DefaultModelId = "grok-4";

    /// <summary>Gets the chat-completions endpoint.</summary>
    public static Uri ChatEndpoint { get; } = new("https://api.x.ai/v1/chat/completions");

    /// <summary>
    /// Gets the configuration keys tried for this provider, most specific first.
    /// </summary>
    /// <remarks>
    /// <c>XAI_API_KEY</c> is what the xAI console prints; the second entry is the accepted spelling in
    /// several shells and CI setups, and accepting both removes a class of "it worked yesterday".
    /// </remarks>
    public static IReadOnlyList<string> CredentialNames { get; } = ["XAI_API_KEY", "GROK_API_KEY"];

    /// <summary>Gets the models this adapter knows, in display order.</summary>
    /// <remarks>
    /// <c>grok-4</c> stays the default because transcripts and sessions name it; <c>grok-4.7</c> is
    /// what the provider's model page now lists (500k context, agentic tool calling), added so a
    /// developer can select and price it. The three older identifiers remain listed because a resumed
    /// session that used one must still resolve.
    /// </remarks>
    public static IReadOnlyList<ModelDescriptor> Catalog { get; } =
    [
        new(ProviderKey, "grok-4", "Grok 4", ContextWindow: 256_000),
        new(ProviderKey, "grok-3", "Grok 3", ContextWindow: 131_072),
        new(ProviderKey, "grok-3-mini", "Grok 3 Mini", ContextWindow: 131_072),
        new(ProviderKey, "grok-4.7", "Grok 4.7", ContextWindow: 500_000),
    ];

    /// <summary>
    /// Finds a catalogue entry, or synthesises one for an identifier the catalogue does not list.
    /// </summary>
    /// <param name="modelId">The model identifier; the catalogue default when null or blank.</param>
    /// <returns>The catalogue entry, or a descriptor that assumes tools and streaming.</returns>
    public static ModelDescriptor DescriptorFor(string? modelId)
    {
        var id = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId.Trim();
        return Catalog.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal))
            ?? new ModelDescriptor(ProviderKey, id, id);
    }
}
