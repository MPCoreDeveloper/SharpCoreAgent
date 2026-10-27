using SharpCoreAgent.Domain.Model;

namespace SharpCoreAgent.Infrastructure.Providers;

/// <summary>
/// The one place that knows which providers exist and what each of them ships.
/// </summary>
/// <remarks>
/// The CLI's <c>models</c> command, the default for <c>--provider</c>, and the switch behind
/// <c>/model provider:id</c> all read this list, so adding a provider is a change in two adjacent files
/// rather than in the argument parser as well.
/// </remarks>
public static class ModelCatalog
{
    /// <summary>Gets the provider used when the developer names none. <c>deepseek</c>.</summary>
    public const string DefaultProvider = DeepSeekChatModel.ProviderKey;

    /// <summary>Gets every provider key the product understands, in display order.</summary>
    public static IReadOnlyList<string> Providers { get; } =
    [
        DeepSeekChatModel.ProviderKey,
        GrokChatModel.ProviderKey,
        OpenAiCompatibleChatModel.ProviderKey,
        DemoChatModel.ProviderKey,
    ];

    /// <summary>Gets every model the product can name without a live query, in display order.</summary>
    public static IReadOnlyList<ModelDescriptor> All { get; } =
    [
        .. DeepSeekChatModel.Catalog,
        .. GrokChatModel.Catalog,
        .. OpenAiCompatibleChatModel.Catalog,
        .. DemoChatModel.Catalog,
    ];

    /// <summary>
    /// Determines whether a provider key is served by an adapter.
    /// </summary>
    /// <param name="provider">The key to test.</param>
    /// <returns><see langword="true"/> when the key is known.</returns>
    public static bool IsKnownProvider(string? provider) =>
        provider is not null && Providers.Contains(provider, StringComparer.Ordinal);

    /// <summary>
    /// Lists one provider's models.
    /// </summary>
    /// <param name="provider">The provider key.</param>
    /// <returns>The models, in display order; empty when the provider ships none.</returns>
    public static IReadOnlyList<ModelDescriptor> ForProvider(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return [.. All.Where(model => string.Equals(model.Provider, provider, StringComparison.Ordinal))];
    }

    /// <summary>
    /// Gets the configuration keys a provider reads its credential from.
    /// </summary>
    /// <param name="provider">The provider key.</param>
    /// <returns>The keys, most specific first; empty for a provider that needs none.</returns>
    /// <exception cref="ArgumentException">The provider is not known.</exception>
    public static IReadOnlyList<string> CredentialNames(string provider) => provider switch
    {
        DeepSeekChatModel.ProviderKey => DeepSeekChatModel.CredentialNames,
        GrokChatModel.ProviderKey => GrokChatModel.CredentialNames,
        OpenAiCompatibleChatModel.ProviderKey => OpenAiCompatibleChatModel.CredentialNames,
        DemoChatModel.ProviderKey => DemoChatModel.CredentialNames,
        _ => throw new ArgumentException($"'{provider}' is not a provider this build knows.", nameof(provider)),
    };

    /// <summary>
    /// Builds the descriptor for a provider and model choice.
    /// </summary>
    /// <param name="provider">The provider key; <see cref="DefaultProvider"/> when null or blank.</param>
    /// <param name="modelId">The model identifier; the provider's default when null or blank.</param>
    /// <returns>The descriptor, taken from the catalogue when the identifier is listed.</returns>
    /// <exception cref="ArgumentException">The provider is not known.</exception>
    public static ModelDescriptor DescriptorFor(string? provider, string? modelId)
    {
        var key = string.IsNullOrWhiteSpace(provider) ? DefaultProvider : provider.Trim();
        return key switch
        {
            DeepSeekChatModel.ProviderKey => DeepSeekChatModel.DescriptorFor(modelId),
            GrokChatModel.ProviderKey => GrokChatModel.DescriptorFor(modelId),
            OpenAiCompatibleChatModel.ProviderKey => OpenAiCompatibleChatModel.DescriptorFor(modelId),
            DemoChatModel.ProviderKey => DemoChatModel.Catalog[0],
            _ => throw new ArgumentException($"'{key}' is not a provider this build knows.", nameof(provider)),
        };
    }

    /// <summary>
    /// Resolves the short names a developer types in a terminal into a provider and a model.
    /// </summary>
    /// <param name="text">The alias, for example <c>ds</c>, <c>r1</c> or <c>grok</c>.</param>
    /// <param name="provider">The provider the alias means.</param>
    /// <param name="modelId">The model the alias means.</param>
    /// <returns><see langword="true"/> when the text is an alias this build knows.</returns>
    /// <remarks>
    /// Aliases exist because the common switch is between "the fast DeepSeek", "the reasoning DeepSeek"
    /// and "Grok", and typing <c>deepseek:deepseek-reasoner</c> every time is how a developer stops
    /// switching. They are matched before a bare model identifier so <c>/model r1</c> cannot be sent to
    /// a provider as a model called <c>r1</c>.
    /// </remarks>
    public static bool TryResolveAlias(string? text, out string provider, out string modelId)
    {
        provider = DefaultProvider;
        modelId = DeepSeekChatModel.DefaultModelId;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        switch (text.Trim().ToLowerInvariant())
        {
            case "ds":
                provider = DeepSeekChatModel.ProviderKey;
                modelId = DeepSeekChatModel.DefaultModelId;
                return true;

            case "r1":
            case "reasoner":
                provider = DeepSeekChatModel.ProviderKey;
                modelId = "deepseek-reasoner";
                return true;

            case "grok":
            case "g4":
                provider = GrokChatModel.ProviderKey;
                modelId = GrokChatModel.DefaultModelId;
                return true;

            case "g3":
                provider = GrokChatModel.ProviderKey;
                modelId = "grok-3";
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Splits a <c>provider:id</c> name, which is how <c>/model</c> takes its argument.
    /// </summary>
    /// <param name="text">The text to split, for example <c>grok:grok-4</c>.</param>
    /// <param name="descriptor">The descriptor, when the text names a known provider.</param>
    /// <returns><see langword="true"/> when the text was a <c>provider:id</c> pair.</returns>
    public static bool TryParseQualified(string? text, out ModelDescriptor? descriptor)
    {
        descriptor = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var separator = text.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == text.Length - 1)
        {
            return false;
        }

        var provider = text[..separator].Trim();
        var modelId = text[(separator + 1)..].Trim();
        if (!IsKnownProvider(provider) || modelId.Length == 0)
        {
            return false;
        }

        descriptor = DescriptorFor(provider, modelId);
        return true;
    }
}
