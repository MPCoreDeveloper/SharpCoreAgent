using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;

namespace SharpCoreAgent.Infrastructure.Providers;

/// <summary>
/// The <c>openai-compatible</c> adapter: any server that speaks the OpenAI chat-completions protocol,
/// at any base address the developer names.
/// </summary>
/// <remarks>
/// This is the adapter that makes the "OpenAI-compatible" claim true rather than aspirational: Ollama,
/// LM Studio, vLLM, a corporate gateway and OpenAI itself all work through it, and the only things it
/// adds to the shared base are an address, a key name and a default model.
/// </remarks>
public sealed class OpenAiCompatibleChatModel : OpenAiCompatibleModelBase
{
    /// <summary>
    /// Creates an adapter for a server whose protocol is OpenAI-compatible.
    /// </summary>
    /// <param name="http">The client to send on; the caller owns it and its timeout must allow a long stream.</param>
    /// <param name="credentials">The credential store the key is read from at request time.</param>
    /// <param name="baseUrl">
    /// The server root, with or without a trailing slash; <c>/chat/completions</c> is appended unless it
    /// is already there.
    /// </param>
    /// <param name="modelId">The provider's model identifier; the catalogue default when null or blank.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The base URL is not an absolute http(s) URL.</exception>
    public OpenAiCompatibleChatModel(
        HttpClient http,
        ICredentialStore credentials,
        string baseUrl,
        string? modelId = null)
        : this(http, credentials, DescriptorFor(modelId), baseUrl)
    {
    }

    /// <summary>
    /// Creates an adapter for a descriptor the caller already resolved, which is how the resolver
    /// honours a model named in a session file.
    /// </summary>
    /// <param name="http">The client to send on; the caller owns it.</param>
    /// <param name="credentials">The credential store the key is read from at request time.</param>
    /// <param name="model">The descriptor to speak for, taken unchanged.</param>
    /// <param name="baseUrl">The server root.</param>
    /// <exception cref="ArgumentException">The base URL is not an absolute http(s) URL.</exception>
    public OpenAiCompatibleChatModel(
        HttpClient http,
        ICredentialStore credentials,
        ModelDescriptor model,
        string baseUrl)
        : base(http, credentials, model, BuildEndpoint(baseUrl), CredentialNames)
    {
    }

    /// <summary>Gets the provider key clients and reports use. <c>openai-compatible</c>.</summary>
    public const string ProviderKey = "openai-compatible";

    /// <summary>Gets the model identifier used when the caller names none.</summary>
    public const string DefaultModelId = "gpt-4o-mini";

    /// <summary>
    /// Gets the configuration keys tried for this provider, most specific first.
    /// </summary>
    /// <remarks>
    /// The last entry lets one variable configure any compatible endpoint, which is what a corporate
    /// gateway tends to want.
    /// </remarks>
    public static IReadOnlyList<string> CredentialNames { get; } =
        ["OPENAI_API_KEY", "SHARPCOREAGENT_OPENAI_COMPATIBLE_API_KEY", "SHARPCOREAGENT_API_KEY"];

    /// <summary>
    /// Gets the catalogue. A compatible endpoint's real models are whatever the server has loaded, so
    /// this lists the default only; <c>--model</c> passes any other identifier through verbatim.
    /// </summary>
    public static IReadOnlyList<ModelDescriptor> Catalog { get; } = [DescriptorFor(null)];

    /// <summary>
    /// Builds the descriptor for a model identifier.
    /// </summary>
    /// <param name="modelId">The provider's identifier; the catalogue default when null or blank.</param>
    /// <returns>A descriptor that declares tools and streaming, which every compatible server provides.</returns>
    public static ModelDescriptor DescriptorFor(string? modelId) => new(
        ProviderKey,
        string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId.Trim(),
        "OpenAI-compatible",
        ContextWindow: 128_000);

    /// <summary>
    /// Normalises a base URL into a chat-completions endpoint.
    /// </summary>
    /// <param name="baseUrl">
    /// An absolute http(s) URL, for example <c>http://localhost:11434/v1</c>; a URL that already ends in
    /// <c>/chat/completions</c> is accepted unchanged.
    /// </param>
    /// <returns>The absolute endpoint.</returns>
    /// <exception cref="ArgumentException">The URL is blank, not absolute, or not http(s).</exception>
    public static Uri BuildEndpoint(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"'{baseUrl}' is not an absolute http(s) URL.", nameof(baseUrl));
        }

        var path = trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/chat/completions";

        return new Uri(path);
    }
}
