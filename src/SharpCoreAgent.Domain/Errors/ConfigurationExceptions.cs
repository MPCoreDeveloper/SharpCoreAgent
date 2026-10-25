#pragma warning disable CA1032 // Exception type carries mandatory context; see SharpCoreAgentException.cs.

using System.Globalization;

namespace SharpCoreAgent.Domain.Errors;

/// <summary>
/// A credential the turn needs is not configured.
/// </summary>
public sealed class ModelCredentialException : SharpCoreAgentException
{
    /// <summary>Creates an exception.</summary>
    /// <param name="configurationKey">The environment variable or configuration key that is missing.</param>
    public ModelCredentialException(string configurationKey)
        : base(string.Create(
            CultureInfo.InvariantCulture,
            $"No credential configured. Set the environment variable {configurationKey} and try again."))
    {
        ConfigurationKey = configurationKey;
    }

    /// <summary>Gets the missing key's name. The value is never known, so it can never leak.</summary>
    public string ConfigurationKey { get; }
}

/// <summary>
/// A session file could not be read or does not satisfy the documented format.
/// </summary>
public sealed class SessionFormatException : SharpCoreAgentException
{
    /// <summary>Creates an exception.</summary>
    /// <param name="message">What is wrong, naming the file and the rule.</param>
    /// <param name="innerException">The parse failure, if any.</param>
    public SessionFormatException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
