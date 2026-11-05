using System.Globalization;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Agents;
using SharpCoreAgent.Infrastructure.Images;
using SharpCoreAgent.Infrastructure.Tools;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// Writes the binary entries of one server answer into the workspace, and says what became of each.
/// </summary>
/// <remarks>
/// A screenshot is the one thing a browser server returns that a transcript cannot carry: base64 in a tool
/// result is unreadable to a model and unaffordable to a turn. So the bytes go to a file and the model gets
/// a path — inside the workspace, where <c>read_file</c>, the index and the developer can all reach them.
/// <para>
/// Nothing here throws. Every reason a write cannot happen is a sentence appended to the entry's own
/// description, because a refused artefact must not cost the call: the model still learns that the server
/// returned an image, what type it was, how big it was and why it is not on disk.
/// </para>
/// <para>
/// The checks run cheapest first — approval, then the per-call count, then a size estimate taken from the
/// base64 length, then the decode, then the measured size — so an answer that is too large is refused
/// before its bytes exist in memory. That ordering is the point of the estimate: <c>Length / 4 * 3</c> bounds
/// the decoded size from above for well-formed base64, so a 40 MiB capture never becomes a 56 MiB string
/// that is then thrown away.
/// </para>
/// </remarks>
internal sealed class McpArtefactSink
{
    /// <summary>Gets the workspace-relative folder artefacts are written to.</summary>
    internal const string Folder = ".sharpcoreagent/artifacts";

    /// <summary>
    /// The formats this client saves, by MIME type.
    /// </summary>
    /// <remarks>
    /// A closed table on purpose: the extension is decided by the MIME type rather than by the bytes or by
    /// anything the server suggested, so a server cannot name a <c>.exe</c> and a reader of the folder knows
    /// what it has. An unlisted type is described instead of saved. FR-004 names five extensions and six
    /// spellings — <c>image/jpeg</c> and <c>image/jpg</c> are the same file.
    /// </remarks>
    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = "png",
        ["image/jpeg"] = "jpg",
        ["image/jpg"] = "jpg",
        ["image/gif"] = "gif",
        ["image/webp"] = "webp",
        ["image/bmp"] = "bmp",
    };

    private readonly Workspace _workspace;
    private readonly ApprovalMode _approval;
    private readonly string _server;
    private readonly string _tool;
    private readonly McpClientOptions _options;
    private readonly List<string> _written = [];

    /// <summary>
    /// Creates a sink for one call.
    /// </summary>
    /// <param name="workspace">The workspace the files are written into.</param>
    /// <param name="approval">How much the developer agreed to before the call ran.</param>
    /// <param name="server">The server's prefix, as the connection names it.</param>
    /// <param name="tool">The tool name the server knows, without the prefix.</param>
    /// <param name="options">The ceilings a saved artefact works within.</param>
    public McpArtefactSink(
        Workspace workspace,
        ApprovalMode approval,
        string server,
        string tool,
        McpClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(options);

        _workspace = workspace;
        _approval = approval;
        _server = server ?? string.Empty;
        _tool = tool ?? string.Empty;
        _options = options;
    }

    /// <summary>Gets the paths written so far, in the order they were written.</summary>
    public IReadOnlyList<string> Written => _written;

    /// <summary>
    /// Saves one binary entry, or explains why it was not saved.
    /// </summary>
    /// <param name="artefact">The entry the server returned.</param>
    /// <returns>The sentence that replaces the entry's description in the flattened answer.</returns>
    /// <remarks>
    /// The returned text is always usable: a saved artefact names its path and size, a refused one keeps its
    /// description and appends the single reason it was refused.
    /// </remarks>
    public string Save(McpArtefact artefact)
    {
        ArgumentNullException.ThrowIfNull(artefact);

        // ReadOnly still runs the tool and still reports the artefact: the difference between a refused write
        // and a lost answer matters, because one is a decision and the other is a lie about the server.
        if (_approval is not (ApprovalMode.Ask or ApprovalMode.AllowAll))
        {
            return Refuse(artefact, "this session may not write");
        }

        if (_written.Count >= _options.MaxArtefactsPerCall)
        {
            return Refuse(
                artefact,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"the call already saved {_options.MaxArtefactsPerCall} artefacts"));
        }

        if (Estimate(artefact.Base64) > _options.MaxArtefactBytes)
        {
            return Refuse(artefact, Ceiling);
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(artefact.Base64);
        }
        catch (FormatException)
        {
            return Refuse(artefact, "the data is not valid base64");
        }

        // The same ceiling again, measured: a base64 string with no padding decodes to more than the estimate
        // allows for, and the rule is about the bytes that would land on disk.
        if (bytes.Length > _options.MaxArtefactBytes)
        {
            return Refuse(artefact, Ceiling);
        }

        if (EnsureRoom(bytes.Length) is { } retention)
        {
            return Refuse(artefact, retention);
        }

        if (!Extensions.TryGetValue(artefact.MimeType, out var extension))
        {
            return Refuse(
                artefact,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{artefact.MimeType} is not a format this client saves"));
        }

        if (!TryName(artefact.Ordinal, extension, out var fullPath, out var error))
        {
            return Refuse(
                artefact,
                string.Create(CultureInfo.InvariantCulture, $"the workspace refused the write ({error})"));
        }

        try
        {
            Write(fullPath, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refuse(
                artefact,
                string.Create(CultureInfo.InvariantCulture, $"the workspace refused the write ({ex.Message})"));
        }

        var reported = _workspace.ToRelative(fullPath);
        _written.Add(reported);
        return Saved(artefact.Description, reported, bytes.Length);
    }

    /// <summary>
    /// Makes room for one new artefact by deleting the oldest files in the dedicated folder.
    /// </summary>
    /// <param name="incoming">The decoded size of the file about to be written.</param>
    /// <returns>A refusal sentence when room could not be made, or <see langword="null"/> when it could.</returns>
    /// <remarks>
    /// The policy is deliberately simple: a 256 MiB ceiling, oldest first, no deduplication. A file that cannot be
    /// deleted — locked by a reader, or owned by another account — stays, and the new artefact is refused with the
    /// measured total rather than silently pushing the folder over its ceiling. Nothing outside the artefacts folder is
    /// touched.
    /// </remarks>
    private string? EnsureRoom(long incoming)
    {
        var folder = Path.Combine(_workspace.Root, Folder.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(folder))
        {
            return null;
        }

        List<FileInfo> files;
        try
        {
            files = [.. new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.TopDirectoryOnly)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the artefacts directory could not be read ({ex.Message})");
        }

        var total = 0L;
        foreach (var file in files)
        {
            try
            {
                total += file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file that vanished between the listing and the size read is not part of the total.
            }
        }

        if (total + incoming <= _options.MaxArtefactDirectoryBytes)
        {
            return null;
        }

        foreach (var file in files
            .Where(file => file.Exists)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.Ordinal))
        {
            if (total + incoming <= _options.MaxArtefactDirectoryBytes)
            {
                break;
            }

            try
            {
                var length = file.Length;
                file.Delete();
                total -= length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep going: one locked file must not stop the older ones behind it from being reclaimed.
            }
        }

        return total + incoming <= _options.MaxArtefactDirectoryBytes
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"the artefacts directory is {CodeIndexFormat.Size(total)} and adding {CodeIndexFormat.Size(incoming)} would exceed its {CodeIndexFormat.Size(_options.MaxArtefactDirectoryBytes)} ceiling; the oldest files could not all be removed");
    }

    /// <summary>
    /// Names the file for one artefact, never reusing a name that already exists.
    /// </summary>
    /// <param name="ordinal">The artefact's position among the saveable entries of the call.</param>
    /// <param name="extension">The extension the MIME type table chose.</param>
    /// <param name="fullPath">The absolute path to write.</param>
    /// <param name="error">Why the path was refused, when it was.</param>
    /// <returns><see langword="true"/> when a name was found and the path is inside the workspace.</returns>
    /// <remarks>
    /// The name is derived, never guessed: <c>&lt;server&gt;-&lt;tool&gt;-&lt;n&gt;.&lt;ext&gt;</c>, with both halves
    /// folded the way a tool name is folded. Ordinals advance rather than overwrite, because the first
    /// capture may already be named in the transcript and a second capture of the same page must not
    /// silently replace the evidence the model is talking about.
    /// </remarks>
    private bool TryName(
        int ordinal,
        string extension,
        out string fullPath,
        out string? error)
    {
        var stem = Stem();
        var candidate = Math.Max(ordinal, 1);
        fullPath = string.Empty;

        while (true)
        {
            var path = string.Create(CultureInfo.InvariantCulture, $"{Folder}/{stem}-{candidate}.{extension}");
            if (!PathSafety.TryResolve(_workspace, path, out var resolved, out error))
            {
                return false;
            }

            if (!File.Exists(resolved))
            {
                fullPath = resolved;
                return true;
            }

            candidate++;
        }
    }

    /// <summary>
    /// Writes the bytes through a temporary file and a rename.
    /// </summary>
    /// <param name="fullPath">The destination, already resolved inside the workspace.</param>
    /// <param name="bytes">The decoded artefact.</param>
    /// <remarks>
    /// The same dance <c>write_file</c> does, for the same reason: a reader must never see half a PNG, and a
    /// failed write must leave no file where a complete one is expected.
    /// </remarks>
    private void Write(string fullPath, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(fullPath);
        var folder = string.IsNullOrEmpty(directory) ? _workspace.Root : directory;
        var temporary = Path.Combine(
            folder,
            string.Create(CultureInfo.InvariantCulture, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp"));

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// Builds the sentence for a saved artefact.
    /// </summary>
    /// <param name="description">The entry's description, which the path and the size are added to.</param>
    /// <param name="relative">The workspace-relative path.</param>
    /// <param name="bytes">The decoded size.</param>
    /// <returns>The description with <c>→ path (size)</c> inside its brackets.</returns>
    private static string Saved(string description, string relative, long bytes)
    {
        var closes = description.EndsWith(']');
        return string.Concat(
            closes ? description[..^1] : description,
            " → ",
            relative,
            " (",
            CodeIndexFormat.Size(bytes),
            closes ? ")]" : ")");
    }

    /// <summary>
    /// Builds the sentence for an artefact that was not saved.
    /// </summary>
    /// <param name="artefact">The entry, whose description is kept verbatim.</param>
    /// <param name="reason">The one reason it was not saved.</param>
    /// <returns>The description followed by <c>· not saved: reason</c>.</returns>
    private static string Refuse(McpArtefact artefact, string reason) =>
        string.Concat(artefact.Description, " · not saved: ", reason);

    /// <summary>Gets the ceiling phrased the way the refusal sentence needs it.</summary>
    private string Ceiling => _options.MaxArtefactBytes >= 1024 * 1024
        ? string.Create(CultureInfo.InvariantCulture, $"larger than {_options.MaxArtefactBytes / (1024 * 1024)} MiB")
        : string.Create(CultureInfo.InvariantCulture, $"larger than {_options.MaxArtefactBytes / 1024} KiB");

    /// <summary>Builds the file name's stem from the server and the tool.</summary>
    /// <returns>The two folded halves joined by a dash.</returns>
    private string Stem()
    {
        var server = McpToolName.Fold(_server);
        var tool = McpToolName.Fold(_tool);
        return string.Concat(
            server.Length == 0 ? "server" : server,
            "-",
            tool.Length == 0 ? "tool" : tool);
    }

    /// <summary>
    /// The decoded size of a base64 string, estimated without decoding it.
    /// </summary>
    /// <param name="base64">The payload as the server sent it.</param>
    /// <returns>The estimate, which is an upper bound for well-formed base64.</returns>
    /// <remarks>
    /// <c>Length / 4 * 3</c> is FR-006's estimate; the trailing <c>=</c> padding is subtracted because it is visible
    /// before the decode, and without that an artefact of exactly the ceiling size would be refused as two bytes over
    /// it. The rule itself lives in <see cref="ImagePayload.EstimateBytes"/> since round 10, because the attach ceiling
    /// and this one must not drift apart; it is kept as a one-line delegation so the ordering above still reads as one
    /// decision.
    /// </remarks>
    private static long Estimate(string base64) => ImagePayload.EstimateBytes(base64);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temporary file is unreachable and a failed cleanup must not mask the write error.
        }
    }
}
