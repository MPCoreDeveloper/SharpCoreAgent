namespace SharpCoreAgent.Infrastructure.Web;

/// <summary>
/// The limits and the permissions one <c>fetch_url</c> works under.
/// </summary>
/// <remarks>
/// Every value here is a ceiling rather than a preference: a fetch that would exceed one of them is cut, refused or
/// reported as such. The record is the tool's only configuration, so a host that wants a different ceiling registers a
/// different instance instead of passing a parameter through four layers.
/// </remarks>
public sealed record WebFetchOptions
{
    /// <summary>Gets the name of the <see cref="System.Net.Http.HttpClient"/> the fetch tool uses.</summary>
    /// <remarks>
    /// A client of its own, never the model adapters' client: that one carries an <c>Authorization</c> header. Fetching a
    /// page with a provider credential attached would hand the key to whoever runs the page.
    /// </remarks>
    public const string HttpClientName = "sharpcoreagent-web";

    /// <summary>Gets the shared defaults: fifteen seconds, five redirects, two mebibytes, sixty thousand characters.</summary>
    public static WebFetchOptions Default { get; } = new();

    /// <summary>
    /// Gets the hosts the developer named on this invocation, which are exempt from the address refusal.
    /// </summary>
    /// <remarks>
    /// Surface syntax only — a host, a <c>host:port</c>, or an IP literal, matched exactly and case-insensitively. No
    /// wildcards, because <c>*.internal</c> is a rule nobody can review, while three named hosts can be read at a glance.
    /// </remarks>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    /// <summary>Gets the wall-clock ceiling for one fetch, redirects and body included. Default fifteen seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Gets how many redirects one fetch follows before it gives up. Default five.</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>Gets how many bytes of a response body are read. Default two mebibytes.</summary>
    /// <remarks>
    /// The ceiling applies to what is read, not to what is announced: a server that promises a gigabyte is cut at this
    /// many bytes instead of being downloaded and then shortened.
    /// </remarks>
    public int MaxBytes { get; init; } = 2 * 1024 * 1024;

    /// <summary>Gets how many characters of readable text a result may carry. Default sixty thousand.</summary>
    /// <remarks>
    /// Lower than the model's tool-result budget on purpose: the caller shortens this result again if a turn has already
    /// spent its room, and a page that arrives already inside the window is the one that stays readable there.
    /// </remarks>
    public int MaxChars { get; init; } = 60_000;

    /// <summary>Gets the user agent the fetch announces. Default <c>sharpcoreagent</c>.</summary>
    /// <remarks>
    /// Named so an operator reading a log can tell an agent from a scanner. It is the one header the tool adds, and it
    /// carries no version, no host name and no path: those identify the machine rather than the client.
    /// </remarks>
    public string UserAgent { get; init; } = "sharpcoreagent";
}
