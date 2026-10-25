using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Model;
using SharpCoreAgent.Domain.Sessions;
using SharpCoreAgent.Infrastructure.Chat;

namespace SharpCoreAgent.Infrastructure.Providers;

/// <summary>
/// The composition point for model adapters: it turns a descriptor into a live model and is the only
/// place that knows which class speaks which protocol.
/// </summary>
/// <remarks>
/// The resolver checks the credential before it builds anything, because a missing key is the most
/// common first-run failure and the cheapest one to explain: the CLI turns
/// <see cref="ModelCredentialException"/> into exit code 3 with the variable's name, before a socket is
/// opened or a turn is started.
/// <para>
/// Every model it returns is wrapped in <see cref="RetryingChatModel"/>, so a caller cannot
/// accidentally get an un-retried adapter; the policy, the clock and the retry callback are injected
/// once, at construction.
/// </para>
/// </remarks>
public sealed class ChatModelResolver : IChatModelResolver
{
    private readonly HttpClient _http;
    private readonly ICredentialStore _credentials;
    private readonly string? _baseUrl;
    private readonly TimeProvider _time;
    private readonly RetryPolicy _policy;
    private readonly Action<RetryAttempt>? _onRetry;

    /// <summary>
    /// Creates a resolver.
    /// </summary>
    /// <param name="http">The client adapters send on; the caller owns it and its timeout.</param>
    /// <param name="credentials">The credential store keys are read from.</param>
    /// <param name="baseUrl">
    /// The endpoint for the <c>openai-compatible</c> provider, normally from <c>--base-url</c>. Null is
    /// allowed until that provider is asked for.
    /// </param>
    /// <param name="time">The clock the retry delay uses; <see cref="TimeProvider.System"/> when null.</param>
    /// <param name="policy">The retry policy; <see cref="RetryPolicy.Default"/> when null.</param>
    /// <param name="onRetry">Called before each retry so the CLI can say "retrying (2/3)".</param>
    public ChatModelResolver(
        HttpClient http,
        ICredentialStore credentials,
        string? baseUrl = null,
        TimeProvider? time = null,
        RetryPolicy? policy = null,
        Action<RetryAttempt>? onRetry = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(credentials);

        _http = http;
        _credentials = credentials;
        // Normalised and validated here so a bad --base-url fails at startup rather than mid-turn.
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : OpenAiCompatibleChatModel.BuildEndpoint(baseUrl).AbsoluteUri;
        _time = time ?? TimeProvider.System;
        _policy = (policy ?? RetryPolicy.Default).Validate();
        _onRetry = onRetry;
    }

    /// <summary>Gets the retry policy every resolved model is wrapped in.</summary>
    public RetryPolicy Policy => _policy;

    /// <inheritdoc />
    /// <exception cref="ModelCredentialException">The provider's credential is not configured.</exception>
    /// <exception cref="ArgumentException">
    /// No adapter serves the descriptor's provider, or the <c>openai-compatible</c> provider was asked
    /// for without a base URL.
    /// </exception>
    public IStreamingChatModel Resolve(ModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(model);
        EnsureCredential(model.Provider);

        IStreamingChatModel adapter = model.Provider switch
        {
            DeepSeekChatModel.ProviderKey => new DeepSeekChatModel(_http, _credentials, model),
            GrokChatModel.ProviderKey => new GrokChatModel(_http, _credentials, model),
            OpenAiCompatibleChatModel.ProviderKey => new OpenAiCompatibleChatModel(
                _http,
                _credentials,
                model,
                _baseUrl ?? throw new ArgumentException(
                    "--base-url is required for the openai-compatible provider.",
                    nameof(model))),
            DemoChatModel.ProviderKey => new DemoChatModel(model),
            _ => throw new ArgumentException(
                $"No adapter serves provider '{model.Provider}'. Known providers: {string.Join(", ", ModelCatalog.Providers)}.",
                nameof(model)),
        };

        return new RetryingChatModel(adapter, _policy, _time, _onRetry);
    }

    /// <summary>
    /// Fails now, rather than after a turn has started, when the provider's key is missing.
    /// </summary>
    /// <param name="provider">The provider key.</param>
    /// <exception cref="ModelCredentialException">None of the provider's keys is set.</exception>
    private void EnsureCredential(string provider)
    {
        var names = ModelCatalog.CredentialNames(provider);
        if (names.Count == 0)
        {
            return;
        }

        foreach (var name in names)
        {
            if (_credentials.Find(name) is not null)
            {
                return;
            }
        }

        throw new ModelCredentialException(names[0]);
    }
}
