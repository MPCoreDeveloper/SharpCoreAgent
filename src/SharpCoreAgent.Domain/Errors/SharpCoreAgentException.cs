// CA1032 wants a parameterless constructor on every exception. These types carry mandatory context
// (which provider, which key, which status code); a parameterless instance would be a lie the caller
// cannot act on, so the standard trio is deliberately not offered.
#pragma warning disable CA1032

using System.Globalization;

namespace SharpCoreAgent.Domain.Errors;

/// <summary>
/// The base type for every failure SharpCoreAgent raises deliberately.
/// </summary>
/// <remarks>
/// A caller that wants one handler for "the product said no" catches this; everything else is a bug
/// or an environment problem. Messages are written for a developer reading a terminal, and must never
/// contain a credential.
/// </remarks>
public class SharpCoreAgentException : Exception
{
    /// <summary>Creates an exception.</summary>
    /// <param name="message">What went wrong.</param>
    public SharpCoreAgentException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with an inner cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public SharpCoreAgentException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A provider call failed, in a way the caller may be able to classify.
/// </summary>
public class ModelException : SharpCoreAgentException
{
    /// <summary>Creates an exception.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="isTransient">Whether retrying the same request, before any output arrived, could succeed.</param>
    /// <param name="retryAfter">The delay the provider asked for, when it said so.</param>
    /// <param name="innerException">The cause, if any.</param>
    public ModelException(string message, bool isTransient = false, TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        IsTransient = isTransient;
        RetryAfter = retryAfter;
    }

    /// <summary>Gets a value indicating whether the failure is worth retrying.</summary>
    public bool IsTransient { get; }

    /// <summary>Gets the delay the provider asked for, when it sent one.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// A provider returned a non-success HTTP status.
/// </summary>
public sealed class ModelRequestException : ModelException
{
    /// <summary>Creates an exception.</summary>
    /// <param name="provider">The provider key.</param>
    /// <param name="modelId">The model identifier.</param>
    /// <param name="statusCode">The HTTP status code.</param>
    /// <param name="message">The provider's message, with any credential redacted.</param>
    /// <param name="isTransient">Whether the status is retryable.</param>
    /// <param name="retryAfter">The delay the provider asked for, when it sent one.</param>
    public ModelRequestException(
        string provider,
        string modelId,
        int statusCode,
        string message,
        bool isTransient,
        TimeSpan? retryAfter = null)
        : base(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{provider}:{modelId} returned HTTP {statusCode}: {message}"),
            isTransient,
            retryAfter)
    {
        Provider = provider;
        ModelId = modelId;
        StatusCode = statusCode;
    }

    /// <summary>Gets the provider key.</summary>
    public string Provider { get; }

    /// <summary>Gets the model identifier.</summary>
    public string ModelId { get; }

    /// <summary>Gets the HTTP status code.</summary>
    public int StatusCode { get; }
}
