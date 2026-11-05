namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// The ceilings a stdio MCP client works within.
/// </summary>
/// <remarks>
/// Every number here answers a question a server could otherwise answer for us: how long a request may
/// take before the session stops waiting, how many pages of tools are worth reading, and how much of one
/// answer reaches the model. No number here is a command-line flag, for the same reason the fetch ceilings
/// are not: a budget is a property of the capability, not of the invocation. <see cref="SamplingPolicy"/> is
/// the exception, because letting a server spend the model budget is an authority decision the developer makes
/// per invocation.
/// </remarks>
public sealed record McpClientOptions
{
    /// <summary>Gets the protocol version this client announces. Default <c>2025-06-18</c>.</summary>
    /// <remarks>
    /// The client sends this and then accepts whatever the server answers, because the versions in the
    /// wild differ in methods this client does not call. Refusing a server over a date would buy nothing.
    /// </remarks>
    public string ProtocolVersion { get; init; } = "2025-06-18";

    /// <summary>Gets the name this client reports in <c>clientInfo</c>. Default <c>sharpcoreagent</c>.</summary>
    public string ClientName { get; init; } = "sharpcoreagent";

    /// <summary>Gets the wall-clock ceiling for one control request. Default twenty seconds.</summary>
    /// <remarks>
    /// Covers <c>initialize</c> and every page of <c>tools/list</c>. A server that cannot answer its
    /// handshake in twenty seconds is not going to serve a turn either.
    /// </remarks>
    public TimeSpan ControlTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Gets the wall-clock ceiling for one <c>tools/call</c>. Default two minutes.</summary>
    /// <remarks>
    /// Longer than a control request because a tool call is real work — a query, a screenshot, a crawl —
    /// and a server that runs one slowly is not the same thing as a server that is broken.
    /// </remarks>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets the most <c>tools/list</c> pages read from one server. Default ten.</summary>
    /// <remarks>
    /// Pagination is the server's choice, so a page ceiling is the only way startup cannot be held open
    /// by a server that never stops offering a next page.
    /// </remarks>
    public int MaxToolPages { get; init; } = 10;

    /// <summary>Gets how many times one connection may restart a server that stopped. Default three.</summary>
    /// <remarks>
    /// A restart happens before the next call, never inside the call that failed: an MCP tool may have
    /// changed something before its server died, and replaying it would turn one side effect into two. The
    /// ceiling is what stops a server that crashes on every start from becoming an infinite loop. Zero
    /// turns supervision off for an embedder that wants the old behaviour.
    /// </remarks>
    public int MaxRestarts { get; init; } = 3;

    /// <summary>Gets what a server may ask the session's model to do. Default <see cref="McpSamplingPolicy.Deny"/>.</summary>
    /// <remarks>
    /// Sampling is a server request, not a tool call: it spends the developer's model budget without the model
    /// deciding to. Deny is therefore the default; the CLI exposes the policy as <c>--mcp-sampling</c>.
    /// </remarks>
    public McpSamplingPolicy SamplingPolicy { get; init; } = McpSamplingPolicy.Deny;

    /// <summary>Gets how many sampling requests one connection may answer in a minute. Default ten.</summary>
    public int MaxSamplingCallsPerMinute { get; init; } = 10;

    /// <summary>Gets how many input tokens sampling may spend in a minute. Default 100 000.</summary>
    public int MaxSamplingInputTokensPerMinute { get; init; } = 100_000;

    /// <summary>Gets the largest output one sampling call may request. Default 25 000.</summary>
    public int MaxSamplingOutputTokensPerCall { get; init; } = 25_000;

    /// <summary>Gets the most characters one MCP result returns. Default 64 000.</summary>
    /// <remarks>
    /// The tool's own natural ceiling; the loop's <c>MaxToolResultChars</c> still applies on top. Both
    /// exist because they answer different questions — this one bounds one call, that one bounds a turn.
    /// </remarks>
    public int MaxResultChars { get; init; } = 64_000;

    /// <summary>Gets how many stderr lines are remembered for the verbose log. Default 200.</summary>
    public int MaxStderrLines { get; init; } = 200;

    /// <summary>Gets the largest decoded artefact one server answer may save. Default 8 MiB.</summary>
    /// <remarks>
    /// Binary content is the one part of a <c>content[]</c> that can be arbitrarily large, so the ceiling is
    /// measured on the bytes rather than on the base64: an answer is refused before it is decoded, which is
    /// the only place the memory can still be saved. A full-page screenshot of a dense page is a few MiB.
    /// </remarks>
    public long MaxArtefactBytes { get; init; } = 8L * 1024 * 1024;

    /// <summary>Gets how many artefacts one call may save. Default eight.</summary>
    /// <remarks>
    /// A screenshot, a snapshot and a handful of images is a normal answer; a hundred is a server that
    /// misunderstood the tool. The remainder is described rather than dropped, so nothing is silent.
    /// </remarks>
    public int MaxArtefactsPerCall { get; init; } = 8;

    /// <summary>Gets the maximum total size of the artefact directory. Default 256 MiB.</summary>
    /// <remarks>
    /// A browser server can run for days, and one screenshot per page is enough to fill a disk. The cap is enforced
    /// before a new file is written: the oldest files in the dedicated artefacts folder are deleted until the new file
    /// fits. There is no deduplication and no second copy: the folder is the record, and aging out its oldest entries is
    /// cheaper than making the transcript unbounded.
    /// </remarks>
    public long MaxArtefactDirectoryBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// Checks every ceiling and returns this instance so it can be used inline.
    /// </summary>
    /// <returns>This instance.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A ceiling is outside its documented range.</exception>
    public McpClientOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(ProtocolVersion))
        {
            throw Invalid(nameof(ProtocolVersion), "the protocol version must be named");
        }

        if (string.IsNullOrWhiteSpace(ClientName))
        {
            throw Invalid(nameof(ClientName), "the client name must be named");
        }

        Require(ControlTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), nameof(ControlTimeout));
        Require(CallTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), nameof(CallTimeout));
        Require(MaxToolPages, 1, 100, nameof(MaxToolPages));
        Require(MaxRestarts, 0, 100, nameof(MaxRestarts));
        Require(MaxSamplingCallsPerMinute, 1, 1_000, nameof(MaxSamplingCallsPerMinute));
        Require(MaxSamplingInputTokensPerMinute, 1_000, 10_000_000, nameof(MaxSamplingInputTokensPerMinute));
        Require(MaxSamplingOutputTokensPerCall, 256, 64_000, nameof(MaxSamplingOutputTokensPerCall));
        Require(MaxResultChars, 1_000, 1_000_000, nameof(MaxResultChars));
        Require(MaxStderrLines, 1, 10_000, nameof(MaxStderrLines));
        Require(MaxArtefactBytes, 1024, 64L * 1024 * 1024, nameof(MaxArtefactBytes));
        Require(MaxArtefactsPerCall, 1, 64, nameof(MaxArtefactsPerCall));
        Require(MaxArtefactDirectoryBytes, 1024 * 1024, 4L * 1024 * 1024 * 1024, nameof(MaxArtefactDirectoryBytes));
        return this;
    }

    /// <summary>
    /// Builds the exception for a property that cannot be used.
    /// </summary>
    /// <param name="name">The property name, carried in <see cref="ArgumentException.ParamName"/>.</param>
    /// <param name="message">What is wrong, phrased for the developer.</param>
    /// <returns>The exception to throw.</returns>
    /// <remarks>
    /// The throw goes through a helper so the exception carries the property name without the analyzer reading
    /// that name as an argument of the method that happened to validate it.
    /// </remarks>
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

    private static void Require(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, value, $"must be between {minimum} and {maximum}");
        }
    }

    private static void Require(long value, long minimum, long maximum, string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, value, $"must be between {minimum} and {maximum}");
        }
    }
}
