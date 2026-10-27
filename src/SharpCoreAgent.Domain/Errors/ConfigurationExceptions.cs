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
/// A credential file could not be read, or a line in it does not follow the documented shape.
/// </summary>
/// <remarks>
/// The message names the file and, for a rejected line, its number. It never repeats the line itself:
/// a line that failed to parse is a line that may be the key, and an exception message travels to
/// logs, terminals and bug reports that a key must not reach.
/// </remarks>
public sealed class CredentialFileException : SharpCoreAgentException
{
    /// <summary>Creates an exception.</summary>
    /// <param name="message">What is wrong, naming the file and the line where there is one.</param>
    /// <param name="innerException">The IO failure, if any.</param>
    public CredentialFileException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The settings file could not be read, written, or a value in it is not one the command line would
/// accept.
/// </summary>
/// <remarks>
/// Separate from <see cref="CredentialFileException"/> on purpose: a broken preferences file is not a
/// broken key, and the two carry different exit codes because the retry a script should attempt is
/// different. The message always names the file, and the key when one value is at fault; it never
/// quotes a value the developer did not type on this command line, because a settings file is where a
/// key must never end up and the message travels further than the file does.
/// </remarks>
public sealed class ConfigurationFileException : SharpCoreAgentException
{
    /// <summary>Creates an exception.</summary>
    /// <param name="message">What is wrong, naming the file and the setting where there is one.</param>
    /// <param name="innerException">The IO or JSON failure, if any.</param>
    public ConfigurationFileException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
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
