using SharpCoreAgent.Domain.Errors;
using SharpCoreAgent.Domain.Sessions;

namespace SharpCoreAgent.Infrastructure.Credentials;

/// <summary>
/// Reads provider credentials from explicit overrides first, then from the process environment.
/// </summary>
/// <remarks>
/// Only names cross this boundary: the value is read at the moment of use, handed straight to the
/// transport's <c>Authorization</c> header and never stored on a descriptor, a snapshot or a report.
/// A blank value counts as unset, because an empty variable is how a developer usually clears a key
/// for one command.
/// </remarks>
public sealed class EnvironmentCredentialStore : ICredentialStore
{
    private readonly IReadOnlyDictionary<string, string> _overrides;
    private readonly Func<string, string?> _readEnvironment;

    /// <summary>
    /// Creates a store.
    /// </summary>
    /// <param name="overrides">
    /// Values supplied through configuration, which win over the environment. Keys are variable names.
    /// </param>
    /// <param name="readEnvironment">
    /// The environment reader; defaults to <see cref="Environment.GetEnvironmentVariable(string)"/> and
    /// exists so tests do not have to mutate the process environment.
    /// </param>
    public EnvironmentCredentialStore(
        IReadOnlyDictionary<string, string>? overrides = null,
        Func<string, string?>? readEnvironment = null)
    {
        _overrides = overrides ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _readEnvironment = readEnvironment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>
    /// Creates a store that reads a credential file, or the environment alone when no file is named.
    /// </summary>
    /// <param name="path">
    /// The file named by <c>--credential-file</c> or <c>SHARPCOREAGENT_CREDENTIAL_FILE</c>, or
    /// <see langword="null"/> when the environment is the only source.
    /// </param>
    /// <returns>
    /// A store whose configuration is the file: a key in it wins over the same key in the environment.
    /// </returns>
    /// <exception cref="Domain.Errors.CredentialFileException">
    /// The file is missing, unreadable or malformed. It is read here, once, so a bad file fails the
    /// command before the first request rather than mid-turn.
    /// </exception>
    public static EnvironmentCredentialStore FromFile(string? path) =>
        new(string.IsNullOrWhiteSpace(path) ? null : CredentialFile.Read(path));

    /// <inheritdoc />
    public string? Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_overrides.TryGetValue(name, out var configured) && !string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var value = _readEnvironment(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <inheritdoc />
    public string GetRequired(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Find(name) ?? throw new ModelCredentialException(name);
    }

    /// <summary>
    /// Determines whether a key is configured, without returning its value.
    /// </summary>
    /// <param name="name">The configuration key.</param>
    /// <returns><see langword="true"/> when a non-blank value exists.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    public bool IsConfigured(string name) => Find(name) is not null;
}
