using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Web;

/// <summary>
/// Fetches a page through the session's browser rather than the HTTP client.
/// </summary>
/// <remarks>
/// The browser is the Playwright MCP server, not a second driver. A session without <c>--browser</c> gets the
/// unavailable implementation, so <c>use_browser: true</c> is an explicit refusal rather than a hidden process start.
/// </remarks>
public interface IBrowserFetcher
{
    /// <summary>Gets a value indicating whether a browser navigation tool is available.</summary>
    bool IsAvailable { get; }

    /// <summary>Navigates to one URL and returns what the browser tool reported.</summary>
    /// <param name="uri">The absolute http/https URL.</param>
    /// <param name="context">The invocation context, carrying the workspace and approval mode.</param>
    /// <param name="cancellationToken">Cancels the navigation.</param>
    /// <returns>The browser's text, or a failure sentence.</returns>
    ValueTask<BrowserFetchResult> FetchAsync(
        Uri uri,
        ToolInvocationContext context,
        CancellationToken cancellationToken);
}

/// <summary>What one browser navigation returned.</summary>
/// <param name="IsError">Whether the browser tool refused or failed.</param>
/// <param name="Text">The browser tool's text.</param>
public sealed record BrowserFetchResult(bool IsError, string Text)
{
    /// <summary>Creates a successful answer.</summary>
    /// <param name="text">The browser's text.</param>
    /// <returns>The result.</returns>
    public static BrowserFetchResult Success(string text) => new(false, text);

    /// <summary>Creates a failed answer.</summary>
    /// <param name="text">Why it failed.</param>
    /// <returns>The result.</returns>
    public static BrowserFetchResult Failure(string text) => new(true, text);
}

/// <summary>The browser path for a session that did not pass <c>--browser</c>.</summary>
public sealed class UnavailableBrowserFetcher : IBrowserFetcher
{
    /// <summary>Gets the shared instance.</summary>
    public static UnavailableBrowserFetcher Instance { get; } = new();

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public ValueTask<BrowserFetchResult> FetchAsync(
        Uri uri,
        ToolInvocationContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(BrowserFetchResult.Failure(
            $"'{uri.AbsoluteUri}' was not opened in a browser: this session did not pass --browser. Re-run with --browser, or call fetch_url with use_browser:false for the pure HTTP path."));
}
