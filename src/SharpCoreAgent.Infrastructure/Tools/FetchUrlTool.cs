using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;
using SharpCoreAgent.Infrastructure.Web;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// Reads one web page and returns it as text.
/// </summary>
/// <remarks>
/// Registered only when the developer passed <c>--web</c>, so a build that asks for nothing sends nothing: this is the
/// only tool in the product that leaves the machine, and its absence is the default rather than its permission.
/// <para>
/// Three decisions shape everything below. The first is that the address is judged before the request: a host that
/// resolves anywhere private is refused, and so is every address a redirect points at, because a public page that
/// redirects to <c>127.0.0.1</c> would otherwise be an open door into whatever the developer is running. The second is
/// that the request carries nothing identifying: one user agent, no cookie jar, no referer, no credential — the client
/// this tool is given is not the model adapters' client, so a provider key cannot be attached to it. The third is that
/// the answer says exactly what happened: the final URL after redirects, the status, the type, how many bytes were read
/// and whether the text was cut, so the model can tell a whole page from a fragment.
/// </para>
/// </remarks>
public sealed class FetchUrlTool : ITool
{
    /// <summary>Gets the tool name.</summary>
    public const string Name = "fetch_url";

    private const string Schema =
        """{"type":"object","properties":{"url":{"type":"string","description":"Absolute http or https URL to read."},"max_chars":{"type":"integer","minimum":1,"maximum":60000,"description":"Longest text to return; defaults to the tool's ceiling."}},"required":["url"],"additionalProperties":false}""";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WebFetchOptions _options;
    private readonly IWebAddressResolver _resolver;

    /// <summary>
    /// Creates the tool.
    /// </summary>
    /// <param name="httpClientFactory">Supplies the fetch client, which carries no credential and no cookie jar.</param>
    /// <param name="options">The ceilings and the hosts the developer named.</param>
    /// <param name="resolver">Turns a host into the addresses the refusal is judged from.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public FetchUrlTool(
        IHttpClientFactory httpClientFactory,
        WebFetchOptions options,
        IWebAddressResolver resolver)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <inheritdoc />
    public ToolDefinition Definition { get; } = new(
        Name,
        "Fetch one absolute http or https URL and return the page as readable text. Reports the final URL after redirects, the status, the content type and whether the text was cut. Refuses loopback, private, link-local and cloud-metadata addresses unless the developer named the host. Sends no cookie and no credential, and never runs JavaScript.",
        Schema);

    /// <inheritdoc />
    /// <remarks>
    /// Read-only in the sense that matters to an approval prompt: it changes nothing on disk and starts no process. It
    /// does leave the machine, which is why the capability is opt-in per invocation rather than a property of the tool.
    /// </remarks>
    public ToolSafety Safety => ToolSafety.ReadOnly;

    /// <inheritdoc />
    public async ValueTask<ToolResult> ExecuteAsync(
        ToolCall toolCall,
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(context);

        using var arguments = ToolArgumentReader.Parse(Name, toolCall.ArgumentsJson);
        var url = arguments.GetRequiredString("url");
        var maxChars = arguments.GetOptionalInt("max_chars", _options.MaxChars, minimum: 1, maximum: _options.MaxChars);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var requested))
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"'{url}' is not an absolute URL. Give one address, scheme included, for example https://example.com/page.");
        }

        // One ceiling for the whole fetch, redirects and body included: a chain of slow hops must not add up to a
        // minute of waiting just because each hop stayed inside a per-request budget.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        var token = timeout.Token;

        var client = _httpClientFactory.CreateClient(WebFetchOptions.HttpClientName);
        var current = requested;
        var hops = 0;

        try
        {
            while (true)
            {
                if (await RefusalAsync(toolCall, current, token).ConfigureAwait(false) is { } refusal)
                {
                    return refusal;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.1");

                using var response = await client
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location is null)
                    {
                        return ToolResult.Failure(
                            toolCall.Id,
                            Name,
                            $"{current.AbsoluteUri} answered {Status(response)} without a Location header, so there is nowhere to follow it.");
                    }

                    hops++;
                    if (hops > _options.MaxRedirects)
                    {
                        return ToolResult.Failure(
                            toolCall.Id,
                            Name,
                            $"gave up after {_options.MaxRedirects} redirects; the last one pointed at {new Uri(current, location).AbsoluteUri}.");
                    }

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                return await AnswerAsync(toolCall, requested, current, hops, response, maxChars, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"no answer from {current.AbsoluteUri} within {_options.Timeout.TotalSeconds:0} seconds."));
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Failure(toolCall.Id, Name, $"{current.AbsoluteUri} could not be reached: {ex.Message}");
        }
    }

    private static readonly string[] TextSuffixes =
        ["json", "xml", "javascript", "ecmascript", "yaml", "toml", "sql", "graphql", "x-sh"];

    /// <summary>
    /// Judges one hop before it is requested.
    /// </summary>
    /// <param name="toolCall">The call being answered, for the identifier on a refusal.</param>
    /// <param name="uri">The address this hop would reach.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The refusal or failure to return, or <see langword="null"/> when the hop may be requested.</returns>
    /// <remarks>
    /// Applied to every hop rather than to the first one, because a redirect is the obvious way around a check that only
    /// read the URL the model wrote.
    /// </remarks>
    private async ValueTask<ToolResult?> RefusalAsync(ToolCall toolCall, Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return ToolResult.Refused(
                toolCall.Id,
                Name,
                $"fetch_url reads http and https only, and '{uri.Scheme}' is neither. Nothing was requested.");
        }

        if (uri.UserInfo.Length > 0)
        {
            return ToolResult.Refused(
                toolCall.Id,
                Name,
                "fetch_url sends no credential, so a URL that carries a user name or password is refused. Use a page that does not need one.");
        }

        if (uri.Host.Length == 0)
        {
            return ToolResult.Failure(toolCall.Id, Name, $"'{uri.AbsoluteUri}' names no host, so there is nothing to fetch.");
        }

        IReadOnlyList<IPAddress> addresses;
        try
        {
            addresses = await _resolver.ResolveAsync(uri.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            return ToolResult.Failure(toolCall.Id, Name, $"{uri.Host} does not resolve: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Failure(toolCall.Id, Name, $"{uri.Host} is not a host this build can resolve: {ex.Message}");
        }

        var blocked = addresses.Select(WebAddressPolicy.Classify).FirstOrDefault(category => category is not null);
        if (blocked is null || WebAddressPolicy.IsExempt(uri.Host, uri.Port, addresses, _options.AllowedHosts))
        {
            return null;
        }

        return ToolResult.Refused(
            toolCall.Id,
            Name,
            string.Create(
                CultureInfo.InvariantCulture,
                $"refused: {uri.Host} resolves to {string.Join(", ", addresses)}, which is {blocked}. fetch_url does not read this machine, its network or its cloud metadata, and no redirect reaches them either. If this host is meant, name it for this invocation: --web-allow {uri.Host}"));
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is 301 or 302 or 303 or 307 or 308;

    /// <summary>
    /// Turns a finished response into the result the model reads.
    /// </summary>
    /// <param name="toolCall">The call being answered.</param>
    /// <param name="requested">The URL the model wrote.</param>
    /// <param name="finalUri">The URL that answered, after redirects.</param>
    /// <param name="hops">How many redirects were followed.</param>
    /// <param name="response">The response, headers read and body not yet consumed.</param>
    /// <param name="maxChars">The character ceiling in force for this call.</param>
    /// <param name="cancellationToken">Cancels the body read.</param>
    /// <returns>The result: the header block, then the text.</returns>
    private async ValueTask<ToolResult> AnswerAsync(
        ToolCall toolCall,
        Uri requested,
        Uri finalUri,
        int hops,
        HttpResponseMessage response,
        int maxChars,
        CancellationToken cancellationToken)
    {
        var contentType = response.Content.Headers.ContentType;
        var mediaType = contentType?.MediaType;
        var (bytes, stoppedByBytes) = await ReadBoundedAsync(response.Content, _options.MaxBytes, cancellationToken).ConfigureAwait(false);

        var header = new StringBuilder();
        header.Append("url          ").Append(finalUri.AbsoluteUri).Append('\n');
        header.Append("status       ").Append(Status(response)).Append('\n');
        header.Append("type         ").Append(mediaType ?? "unknown");
        if (!string.IsNullOrWhiteSpace(contentType?.CharSet))
        {
            header.Append("; charset=").Append(contentType.CharSet.Trim('"'));
        }

        header.Append('\n');
        header.Append("bytes        ").Append(bytes.Length.ToString(CultureInfo.InvariantCulture));
        if (stoppedByBytes)
        {
            header.Append(" (read stopped at the ")
                .Append(_options.MaxBytes.ToString(CultureInfo.InvariantCulture))
                .Append("-byte ceiling)");
        }

        if (hops > 0)
        {
            header.Append('\n').Append("redirected   from ").Append(requested.AbsoluteUri);
        }

        if (!IsTextual(mediaType, bytes))
        {
            return ToolResult.Failure(
                toolCall.Id,
                Name,
                $"{header}\n\nthe response is {mediaType ?? "of an unknown type"}, which fetch_url does not read: it returns text and never downloads a binary. Cite the URL and let the developer open it.");
        }

        var text = EncodingFor(contentType?.CharSet, bytes)
            .GetString(bytes)
            .TrimStart('\uFEFF')
            .TrimEnd();

        var readable = IsHtml(mediaType) ? HtmlToText.Reduce(text) : text;
        var truncated = stoppedByBytes;
        if (readable.Length > maxChars)
        {
            var total = readable.Length;
            readable = string.Create(
                CultureInfo.InvariantCulture,
                $"{readable[..maxChars]}\n[truncated: kept the first {maxChars} of {total} characters]");
            truncated = true;
        }

        if (truncated)
        {
            header.Append('\n').Append("truncated    yes");
        }

        var content = $"{header}\n\n{readable}";
        if ((int)response.StatusCode >= 400)
        {
            return ToolResult.Failure(toolCall.Id, Name, content) with { IsTruncated = truncated };
        }

        return ToolResult.Success(toolCall.Id, Name, content, new ToolEffect(ToolEffectKind.Read, finalUri.AbsoluteUri))
            with
        { IsTruncated = truncated };
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> of a body.
    /// </summary>
    /// <param name="content">The response body.</param>
    /// <param name="maxBytes">The ceiling.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes kept, and whether the body was longer than the ceiling.</returns>
    /// <remarks>
    /// Chunked rather than `ReadAsByteArrayAsync` on purpose: the ceiling has to bound what the process downloads, not
    /// what it returns, or a server announcing a gigabyte would still be downloaded in full before being shortened.
    /// </remarks>
    private static async Task<(byte[] Bytes, bool Exceeded)> ReadBoundedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var collected = new MemoryStream();

        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return (collected.ToArray(), false);
            }

            var room = maxBytes - (int)collected.Length;
            if (read > room)
            {
                await collected.WriteAsync(buffer.AsMemory(0, Math.Max(room, 0)), cancellationToken).ConfigureAwait(false);
                return (collected.ToArray(), true);
            }

            await collected.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsHtml(string? mediaType) =>
        mediaType is not null &&
        (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
         mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));

    private static bool IsTextual(string? mediaType, ReadOnlySpan<byte> bytes)
    {
        if (mediaType is null)
        {
            // No type at all: the bytes themselves decide, and a NUL in the sample is the cheapest reason to refuse.
            return !TextLimits.LooksBinary(bytes);
        }

        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TextSuffixes.Any(suffix => mediaType.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static Encoding EncodingFor(string? charset, ReadOnlySpan<byte> bytes)
    {
        // A byte-order mark beats a header that disagrees with it: the bytes were written by the producer, the header
        // by whoever configured the server.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"', ' '));
            }
            catch (ArgumentException)
            {
                // An encoding this build does not carry is not a reason to fail the fetch: UTF-8 is what the web is.
            }
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static string Status(HttpResponseMessage response)
    {
        var code = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(response.ReasonPhrase) ? code : $"{code} {response.ReasonPhrase}";
    }
}
