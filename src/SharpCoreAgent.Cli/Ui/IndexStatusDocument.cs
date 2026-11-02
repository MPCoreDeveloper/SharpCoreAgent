using System.Globalization;
using System.Text.Json;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Index;

namespace SharpCoreAgent.Cli;

/// <summary>
/// The one builder of the index status object, and the formatters that decide how a status reads.
/// </summary>
/// <remarks>
/// Extracted because two surfaces now publish the same facts: <c>index --status --json</c> in a terminal and
/// <c>/api/status.json</c> on the panel's socket. Two methods building the same dictionary is a disagreement waiting for
/// the first field somebody adds to one of them, and the formatters are here for the same reason one level down — a page
/// that printed <c>Rich</c> where the CLI prints <c>rich</c> is a one-character difference no field-set test would catch.
/// <para>
/// <see cref="Serialize"/> is <c>JsonSerializer.Serialize</c> with no options, exactly what
/// <c>IndexCommand.WriteJson</c> did, which is what makes a byte-for-byte comparison between the two renderings an
/// honest assertion rather than an approximate one.
/// </para>
/// </remarks>
internal static class IndexStatusDocument
{
    /// <summary>
    /// Builds the object <c>index --status --json</c> prints and <c>/api/status.json</c> answers with.
    /// </summary>
    /// <param name="workspace">The workspace the index describes.</param>
    /// <param name="status">The status read from the open index.</param>
    /// <returns>The twenty keys, in the order the contract lists them.</returns>
    /// <remarks>
    /// The key list is the contract at <c>specs/002-code-evidence-index/contracts/cli-index.md</c>. Two of the values are
    /// not stored on <see cref="IndexStatus"/> and are derived here so no caller has to: <c>incomplete</c> is the
    /// freshness state read as a question, and <c>notes</c> is the empty list the contract promised before anything
    /// wrote one.
    /// </remarks>
    internal static Dictionary<string, object?> Build(Workspace workspace, IndexStatus status)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(status);

        return new Dictionary<string, object?>
        {
            ["command"] = "index",
            ["action"] = "status",
            ["workspace"] = Slash(workspace.Root),
            ["state"] = State(status.Freshness),
            ["tier"] = TierName(status.Tier),
            ["embedder"] = status.Embedder,
            ["dimensions"] = status.Dimensions,
            ["files"] = status.Files,
            ["stale"] = status.StaleFiles,
            ["skipped"] = status.SkippedFiles,
            ["orphans"] = status.OrphanRows,
            ["passages"] = status.Chunks,
            ["symbols"] = status.Symbols,
            ["edges"] = status.Edges,
            ["communities"] = status.Communities,
            ["sizeBytes"] = status.SizeBytes,
            ["vectorSearch"] = status.VectorSearchEnabled,
            ["builtUtc"] = status.BuiltUtc?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["incomplete"] = status.Freshness is IndexFreshness.Partial,
            ["notes"] = Array.Empty<string>(),
        };
    }

    /// <summary>
    /// Renders a document as the one line of JSON both surfaces publish.
    /// </summary>
    /// <param name="document">A document from <see cref="Build"/>.</param>
    /// <returns>The JSON text, without a trailing newline.</returns>
    internal static string Serialize(Dictionary<string, object?> document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document);
    }

    /// <summary>
    /// Renders a count with groups of three separated by a space.
    /// </summary>
    /// <param name="value">The count.</param>
    /// <returns>For example <c>1 908</c>.</returns>
    /// <remarks>
    /// A space rather than a comma because the block sits in a terminal beside other spaced numbers, and because the
    /// page reads a thousand files as <c>1 908</c> in every one of its rows.
    /// </remarks>
    internal static string Number(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ');

    /// <summary>
    /// Renders a byte count with one decimal place.
    /// </summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <returns>For example <c>14.2 MiB</c>.</returns>
    internal static string Size(long bytes) => bytes switch
    {
        < 0 => "0 B",
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:0.0} KiB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024):0.0} MiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024 * 1024):0.0} GiB"),
    };

    /// <summary>
    /// Renders the freshness word the blocks and the page use.
    /// </summary>
    /// <param name="freshness">The state to render.</param>
    /// <returns>The lowercase name.</returns>
    /// <remarks>
    /// Shared with <c>mcp serve</c>'s ready line and with the page's headline rather than copied: two renderings of one
    /// enum would drift, and the word a developer reads in a terminal, the word a client's log holds and the word on the
    /// page must be the same word.
    /// </remarks>
    internal static string State(IndexFreshness freshness) => freshness switch
    {
        IndexFreshness.Missing => "missing",
        IndexFreshness.Building => "building",
        IndexFreshness.Partial => "partial",
        IndexFreshness.Stale => "stale",
        IndexFreshness.Fresh => "fresh",
        IndexFreshness.Corrupt => "corrupt",
        _ => "unknown",
    };

    /// <summary>
    /// Renders the tier name the output uses.
    /// </summary>
    /// <param name="tier">The tier to render.</param>
    /// <returns><c>lean</c> or <c>rich</c>.</returns>
    internal static string TierName(IndexTier tier) => tier == IndexTier.Rich ? "rich" : "lean";

    /// <summary>
    /// Renders a path with forward slashes, which is how every block prints one.
    /// </summary>
    /// <param name="path">The path to render.</param>
    /// <returns>The path with <c>/</c> separators.</returns>
    /// <remarks>
    /// Normalised at the edge rather than in the page: the JSON a script reads and the page a developer looks at carry
    /// the same separators, so a workspace path copied out of one is recognisable in the other.
    /// </remarks>
    internal static string Slash(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Replace('\\', '/');
    }
}
