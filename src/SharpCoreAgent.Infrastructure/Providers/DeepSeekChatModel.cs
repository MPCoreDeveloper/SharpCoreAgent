using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;

namespace SharpCoreAgent.Infrastructure.Providers;

/// <summary>
/// The DeepSeek adapter: an OpenAI-compatible server at a fixed address, with a catalogue that records
/// what each of its models can actually do.
/// </summary>
/// <remarks>
/// The catalogue is the reason this class exists rather than a plain
/// <see cref="OpenAiCompatibleChatModel"/>: <c>deepseek-reasoner</c> streams reasoning and refuses tool
/// calls, and declaring that here turns "the model ignored the tools" into a refusal before a request
/// is spent.
/// </remarks>
public sealed class DeepSeekChatModel : OpenAiCompatibleModelBase
{
    /// <summary>Creates the adapter for a catalogue model.</summary>
    /// <param name="http">The client to send on; the caller owns it.</param>
    /// <param name="credentials">The credential store the key is read from at request time.</param>
    /// <param name="modelId">The model identifier; <see cref="DefaultModelId"/> when null or blank.</param>
    public DeepSeekChatModel(HttpClient http, ICredentialStore credentials, string? modelId = null)
        : this(http, credentials, DescriptorFor(modelId))
    {
    }

    /// <summary>Creates the adapter for a descriptor the caller already resolved.</summary>
    /// <param name="http">The client to send on; the caller owns it.</param>
    /// <param name="credentials">The credential store the key is read from at request time.</param>
    /// <param name="model">The descriptor to speak for, taken unchanged.</param>
    public DeepSeekChatModel(HttpClient http, ICredentialStore credentials, ModelDescriptor model)
        : base(http, credentials, model, ChatEndpoint, CredentialNames)
    {
    }

    /// <summary>Gets the provider key. <c>deepseek</c>.</summary>
    public const string ProviderKey = "deepseek";

    /// <summary>Gets the model used when the caller names none.</summary>
    public const string DefaultModelId = "deepseek-chat";

    /// <summary>Gets the chat-completions endpoint.</summary>
    public static Uri ChatEndpoint { get; } = new("https://api.deepseek.com/chat/completions");

    /// <summary>Gets the configuration keys tried for this provider, most specific first.</summary>
    public static IReadOnlyList<string> CredentialNames { get; } = ["DEEPSEEK_API_KEY"];

    /// <summary>
    /// Gets the models this adapter knows, in display order.
    /// </summary>
    /// <remarks>
    /// Context windows are the advertised ones. <c>deepseek-reasoner</c> declares
    /// <see cref="ModelDescriptor.SupportsTools"/> false because the provider rejects tool definitions
    /// on that model; the agent turns that into a clear refusal instead of a bad request.
    /// <para>
    /// The first two identifiers are the ones this build shipped before the provider's page moved on:
    /// <c>deepseek-chat</c> is still the default because a transcript records it, and a build that
    /// could not name it could not resume the sessions it wrote. The two below them are what the
    /// published model list now contains (1M context, tool calls, thinking by default), added so
    /// <c>/cost</c> can price a model a developer can actually choose.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ModelDescriptor> Catalog { get; } =
    [
        new(ProviderKey, "deepseek-chat", "DeepSeek Chat", ContextWindow: 128_000),
        new(ProviderKey, "deepseek-reasoner", "DeepSeek Reasoner", ContextWindow: 128_000, SupportsTools: false, EmitsThinking: true),
        new(ProviderKey, "deepseek-flash", "DeepSeek Flash", ContextWindow: 1_000_000, EmitsThinking: true),
        new(ProviderKey, "deepseek-v4-pro", "DeepSeek V4 Pro", ContextWindow: 1_000_000, EmitsThinking: true),
    ];

    /// <summary>
    /// Finds a catalogue entry, or synthesises one for an identifier the catalogue does not list.
    /// </summary>
    /// <param name="modelId">The model identifier; the catalogue default when null or blank.</param>
    /// <returns>
    /// The catalogue entry, or a descriptor that assumes tools and streaming — the fallback exists so a
    /// model released after this build still works, and it is named as unlisted nowhere in the transcript.
    /// </returns>
    public static ModelDescriptor DescriptorFor(string? modelId)
    {
        var id = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId.Trim();
        return Catalog.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal))
            ?? new ModelDescriptor(ProviderKey, id, id);
    }
}
