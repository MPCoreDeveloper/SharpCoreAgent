namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// What a server may ask this client to do on its behalf when it sends <c>sampling/createMessage</c>.
/// </summary>
/// <remarks>
/// Deny is the default because a server request spends the developer's model budget. <see cref="First"/> allows exactly
/// one sampling request per server connection; <see cref="Unrestricted"/> allows requests until the per-minute ceilings
/// in <see cref="McpClientOptions"/> are reached. Neither mode changes the model: the session's current model answers,
/// so <c>/model</c> remains the developer's choice.
/// </remarks>
public enum McpSamplingPolicy
{
    /// <summary>Never answer a sampling request; the server receives <c>-32601</c>.</summary>
    Deny,

    /// <summary>Answer one sampling request per connection, then refuse the rest.</summary>
    First,

    /// <summary>Answer within the rate limits.</summary>
    Unrestricted,
}
