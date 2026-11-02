using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Merges <c>mcp config --write</c>'s block into the file a client reads, instead of printing it for a redirect.
/// </summary>
/// <remarks>
/// The block is a document for one server; a file is a document that may already hold others, and <c>.mcp.json</c> may
/// hold both keys two different clients read. So the write merges at three levels: the top-level object keeps every
/// key, the client's section keeps every other server, and this entry keeps every field a developer added to it — an
/// <c>env</c> block, a <c>cwd</c>. A file that cannot be merged is refused with nothing written, because a truncated
/// configuration that still parses is worse than a refusal: the next command reads it and believes it.
/// <para>
/// Nothing here writes through a temporary file and a rename. The entry this command writes is one key in a file whose
/// loss costs a re-run of a command with no side effect, and a temporary file beside a developer's <c>.mcp.json</c> is
/// a file their client may try to read.
/// </para>
/// </remarks>
internal static class McpConfigWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Writes the entry into the client's file, keeping everything the file already holds.
    /// </summary>
    /// <param name="client">Whose configuration is being written; it decides the key and the <c>type</c>.</param>
    /// <param name="destination">The file, from <see cref="McpClientSnippets.Destination"/>.</param>
    /// <param name="command">The executable the entry names.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>What was done, and the one line that goes to stderr.</returns>
    /// <exception cref="WorkspaceWriteException">The file could not be read, written or merged.</exception>
    internal static (WriteOutcome Outcome, string Note) Write(
        McpClientKind client,
        string destination,
        string command,
        IReadOnlyList<string> arguments)
    {
        if (!McpClientSnippets.TryShape(client, out var root, out var typed))
        {
            // Unreachable from the command line: FinishMcp refuses --write for the client whose file is TOML, before a
            // workspace is opened. It stays so a caller that arrives another way is told rather than silently ignored.
            throw new WorkspaceWriteException($"{client}'s configuration is not JSON, so there is nothing to merge.");
        }

        var existed = File.Exists(destination);
        var document = Read(destination, existed);
        var section = Section(document, root, destination);
        var entry = McpClientSnippets.Entry(typed, command, arguments);

        WriteOutcome outcome;
        if (section[McpClientSnippets.ServerName] is JsonObject current)
        {
            // Compared as text rather than against a fresh document, because the entry may carry fields this command
            // does not own: an env block that survived the merge is the reason "unchanged" is a claim worth making.
            var before = current.ToJsonString(Indented);
            Merge(current, entry);

            if (string.Equals(before, current.ToJsonString(Indented), StringComparison.Ordinal))
            {
                return (WriteOutcome.Unchanged, Note(WriteOutcome.Unchanged, root, destination));
            }

            outcome = WriteOutcome.Updated;
        }
        else
        {
            section[McpClientSnippets.ServerName] = entry;
            outcome = WriteOutcome.Created;
        }

        Save(destination, document);
        return (outcome, Note(outcome, root, destination));
    }

    /// <summary>
    /// Reads the file, or starts one when it holds nothing yet.
    /// </summary>
    /// <param name="destination">The file.</param>
    /// <param name="existed">Whether it was there before this command ran.</param>
    /// <returns>The document to merge into.</returns>
    /// <exception cref="WorkspaceWriteException">The file could not be read, or is not a JSON object.</exception>
    private static JsonObject Read(string destination, bool existed)
    {
        if (!existed)
        {
            return new JsonObject();
        }

        var text = WorkspaceFileWriter.ReadText(destination);

        if (string.IsNullOrWhiteSpace(text))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(text) switch
            {
                JsonObject document => document,
                null => new JsonObject(),
                _ => throw new WorkspaceWriteException(
                    $"{destination} holds a JSON value that is not an object; nothing was written."),
            };
        }
        catch (JsonException exception)
        {
            throw new WorkspaceWriteException(
                $"{destination} is not valid JSON, so nothing was written: {exception.Message}");
        }
    }

    /// <summary>
    /// Finds the object the client reads under <paramref name="root"/>, adding it when the file has no such key.
    /// </summary>
    /// <param name="document">The file.</param>
    /// <param name="root">The key: <c>mcpServers</c> or <c>servers</c>.</param>
    /// <param name="destination">The file, for the refusal.</param>
    /// <returns>The section to merge into.</returns>
    /// <exception cref="WorkspaceWriteException">A key of that name holds something other than an object.</exception>
    private static JsonObject Section(JsonObject document, string root, string destination)
    {
        switch (document[root])
        {
            case JsonObject section:
                return section;

            case { } other:
                throw new WorkspaceWriteException(
                    $"'{root}' in {destination} holds a JSON {other.GetValueKind()}, not an object; nothing was written.");

            default:
                var created = new JsonObject();
                document[root] = created;
                return created;
        }
    }

    /// <summary>
    /// Copies the entry's fields onto the one the file already had, leaving every other field alone.
    /// </summary>
    /// <param name="existing">The entry the file holds.</param>
    /// <param name="entry">The entry this binary would print.</param>
    /// <remarks>
    /// A developer who added <c>"env"</c> to the entry, or a <c>cwd</c>, meant it; the parts that describe this binary
    /// — <c>type</c>, <c>command</c>, <c>args</c> — are the parts that go stale, and those are the ones overwritten.
    /// <see cref="JsonNode.DeepClone"/> because a node belongs to one parent, and these already belong to the entry.
    /// </remarks>
    private static void Merge(JsonObject existing, JsonObject entry)
    {
        foreach (var (name, value) in entry)
        {
            existing[name] = value?.DeepClone();
        }
    }

    /// <summary>
    /// Writes the document back, without a byte-order mark and with a trailing newline.
    /// </summary>
    /// <param name="destination">The file.</param>
    /// <param name="document">The merged document.</param>
    /// <exception cref="WorkspaceWriteException">The directory or the file could not be written.</exception>
    /// <remarks>
    /// The bytes are <see cref="WorkspaceFileWriter.Save"/>'s: <c>.vscode</c> created when it is missing, one
    /// <c>WriteAllBytes</c>, and an OS failure turned into one message naming the file.
    /// </remarks>
    private static void Save(string destination, JsonObject document) =>
        WorkspaceFileWriter.Save(destination, document.ToJsonString(Indented) + Environment.NewLine);

    /// <summary>Says what happened, in the one line that goes to stderr.</summary>
    /// <param name="outcome">What was done.</param>
    /// <param name="root">The key the entry sits under.</param>
    /// <param name="destination">The file.</param>
    /// <returns>One line, without a newline of its own.</returns>
    private static string Note(WriteOutcome outcome, string root, string destination)
    {
        var key = $"{root}.{McpClientSnippets.ServerName}";

        return outcome switch
        {
            WriteOutcome.Created =>
                $"{McpClientSnippets.NotePrefix}wrote {destination} (created: {key} names this binary)",
            WriteOutcome.Updated =>
                $"{McpClientSnippets.NotePrefix}wrote {destination} (updated: {key} named another path or other arguments)",
            _ =>
                $"{McpClientSnippets.NotePrefix}{destination} already names this binary under {key}; nothing written",
        };
    }
}


