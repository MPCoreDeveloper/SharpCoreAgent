namespace SharpCoreAgent.Infrastructure.McpServer;

/// <summary>
/// What a stdio MCP server answers with, and how much work it will do to keep those answers current.
/// </summary>
/// <remarks>
/// The mirror of <c>McpClientOptions</c>: every value here answers a question a client could otherwise answer
/// for us — which version to claim when the client asks for one this build does not know, how fresh the index
/// has to be before an answer is allowed out, and how often the server is willing to look. None of them is a
/// command-line switch except <c>--no-refresh</c>, for the same reason a fetch ceiling is not one: a budget is
/// a property of the capability, not of the invocation.
/// </remarks>
public sealed record McpServerOptions
{
    /// <summary>The protocol version this build speaks when a client asks for one it does not know.</summary>
    /// <remarks>
    /// A client that sends no <c>protocolVersion</c>, or sends a date this build has never seen, is answered
    /// with this: the version our own client sends, which is the one the dogfood test exercises.
    /// </remarks>
    public const string DefaultProtocolVersion = "2025-06-18";

    /// <summary>Gets the name this server reports in <c>serverInfo</c>. Default <c>sharpcoreagent</c>.</summary>
    public string ServerName { get; init; } = "sharpcoreagent";

    /// <summary>Gets the version this server reports. Default: the assembly version, as three parts.</summary>
    public string ServerVersion { get; init; } =
        typeof(McpServerOptions).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    /// <summary>Gets the version used when the client names one this build does not know.</summary>
    public string ProtocolVersion { get; init; } = DefaultProtocolVersion;

    /// <summary>Gets the protocol versions echoed back verbatim when a client asks for them.</summary>
    /// <remarks>
    /// Only the revisions this server has actually been written against belong here. Echoing a version this
    /// build does not implement would be a promise in a field the client trusts, so an unknown date falls back
    /// to <see cref="ProtocolVersion"/> instead.
    /// </remarks>
    public IReadOnlyList<string> KnownProtocolVersions { get; init; } = ["2024-11-05", "2025-03-26", "2025-06-18"];

    /// <summary>
    /// Gets a value indicating whether the index is brought up to date before an evidence call answers.
    /// Default true; <c>--no-refresh</c> turns it off for a read-only or shared checkout.
    /// </summary>
    public bool Refresh { get; init; } = true;

    /// <summary>Gets the wall-clock ceiling for one refresh. Default three seconds.</summary>
    /// <remarks>
    /// A caller waiting on a tool call is waiting on this too, so the budget is the answer's budget. When it is
    /// spent the call still answers — from the index that is there — and the freshness word in the tool's own
    /// heading says what happened.
    /// </remarks>
    public TimeSpan RefreshBudget { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Gets the shortest gap between two refreshes. Default five seconds.</summary>
    /// <remarks>
    /// An agent asking six related questions in a row is one editing session, not six reasons to walk the
    /// working tree.
    /// </remarks>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Checks every value and returns this instance so it can be used inline.
    /// </summary>
    /// <returns>This instance.</returns>
    /// <exception cref="ArgumentException">A name is blank, or the version is not one this build knows.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A budget is outside its documented range.</exception>
    public McpServerOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerName))
        {
            throw Invalid(nameof(ServerName), "the server name must be named");
        }

        if (string.IsNullOrWhiteSpace(ServerVersion))
        {
            throw Invalid(nameof(ServerVersion), "the server version must be named");
        }

        if (KnownProtocolVersions.Count == 0)
        {
            throw Invalid(nameof(KnownProtocolVersions), "at least one protocol version must be known");
        }

        if (!KnownProtocolVersions.Contains(ProtocolVersion, StringComparer.Ordinal))
        {
            throw Invalid(
                nameof(ProtocolVersion),
                $"'{ProtocolVersion}' is the fallback but is not in {nameof(KnownProtocolVersions)}");
        }

        Require(RefreshBudget, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30), nameof(RefreshBudget));
        Require(RefreshInterval, TimeSpan.Zero, TimeSpan.FromMinutes(5), nameof(RefreshInterval));
        return this;
    }

    /// <summary>
    /// Builds the exception for a property that cannot be used.
    /// </summary>
    /// <param name="name">The property name, carried in <see cref="ArgumentException.ParamName"/>.</param>
    /// <param name="message">What is wrong, phrased for the developer.</param>
    /// <returns>The exception to throw.</returns>
    private static ArgumentException Invalid(string name, string message) => new(message, name);

    private static void Require(TimeSpan value, TimeSpan minimum, TimeSpan maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"must be between {minimum.TotalSeconds} and {maximum.TotalSeconds} seconds");
        }
    }
}
