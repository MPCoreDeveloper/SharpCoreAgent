using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpCoreAgent.Cli;

/// <summary>
/// <c>ide agent --write</c> and <c>ide tasks --write</c>: the two merge rules, and the one line each reports.
/// </summary>
/// <remarks>
/// The rules are different because the files are. A profile is whole-file ownership — a marker on the last line says
/// this command wrote it, and the replacement is total, because YAML frontmatter plus prose has no slots to merge into
/// and a rewriter that loses a sentence is worse than a refusal. A task list is structural: it is JSON, and the thing
/// this command owns is one task found by its label, so everything else in the document survives.
/// <para>
/// Both compare before they write. A file that already says this is not touched, which is what keeps a developer's
/// formatting — or their line endings — from being silently normalised by a second run.
/// </para>
/// </remarks>
internal static class IdeFileWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Writes the agent profile, replacing a file this command wrote before.
    /// </summary>
    /// <param name="destination">The file, from <see cref="AgentProfile.Destination"/>.</param>
    /// <param name="content">The whole profile, from <see cref="AgentProfile.Render"/>.</param>
    /// <returns>What was done, and the one line that goes to stderr.</returns>
    /// <exception cref="WorkspaceWriteException">
    /// The file exists and was not written by this command, or it could not be read or written.
    /// </exception>
    internal static (WriteOutcome Outcome, string Note) WriteProfile(string destination, string content)
    {
        if (!File.Exists(destination))
        {
            WorkspaceFileWriter.Save(destination, content);

            return (WriteOutcome.Created, string.Create(
                CultureInfo.InvariantCulture,
                $"{AgentProfile.NotePrefix}wrote {destination} (created: a profile that asks for this server's five tools)"));
        }

        var existing = WorkspaceFileWriter.ReadText(destination);

        if (!AgentProfile.IsOurs(existing))
        {
            throw new WorkspaceWriteException(string.Create(
                CultureInfo.InvariantCulture,
                $"{destination} exists and was not written by this command, so it was left as it was; delete it, or move it aside, and run again."));
        }

        if (string.Equals(existing, content, StringComparison.Ordinal))
        {
            return (WriteOutcome.Unchanged, string.Create(
                CultureInfo.InvariantCulture,
                $"{AgentProfile.NotePrefix}{destination} already says this; nothing written"));
        }

        WorkspaceFileWriter.Save(destination, content);

        return (WriteOutcome.Updated, string.Create(
            CultureInfo.InvariantCulture,
            $"{AgentProfile.NotePrefix}wrote {destination} (updated: the profile was there and its text changed)"));
    }

    /// <summary>
    /// Files the task into the workspace's task list, keeping every other task and every other key.
    /// </summary>
    /// <param name="destination">The file, from <see cref="WorkspaceTasks.Destination"/>.</param>
    /// <param name="task">The entry, from <see cref="WorkspaceTasks.Task"/>.</param>
    /// <returns>What was done, and the one line that goes to stderr.</returns>
    /// <exception cref="WorkspaceWriteException">
    /// The file is not JSON, is not an object, holds <c>tasks</c> as something that is not an array, or could not be read
    /// or written.
    /// </exception>
    internal static (WriteOutcome Outcome, string Note) WriteTasks(string destination, JsonObject task)
    {
        if (!File.Exists(destination))
        {
            WorkspaceFileWriter.Save(destination, WorkspaceTasks.Render());

            return (WriteOutcome.Created, Filed(destination));
        }

        var text = WorkspaceFileWriter.ReadText(destination);
        var document = Parse(text, destination);
        var tasks = Tasks(document, destination);

        var index = IndexOf(tasks, WorkspaceTasks.Label);
        if (index < 0)
        {
            tasks.Add(task.DeepClone());
        }
        else
        {
            // Replaced in place, so the task keeps the position the developer found it in. Cloned, because a node
            // belongs to one parent and this one is about to belong to the array.
            tasks[index] = task.DeepClone();
        }

        if (document["version"] is null)
        {
            document["version"] = WorkspaceTasks.FormatVersion;
        }

        var merged = document.ToJsonString(Indented) + Environment.NewLine;
        if (string.Equals(merged, text, StringComparison.Ordinal))
        {
            return (WriteOutcome.Unchanged, string.Create(
                CultureInfo.InvariantCulture,
                $"{WorkspaceTasks.NotePrefix}{destination} already carries the task '{WorkspaceTasks.Label}'; nothing written"));
        }

        WorkspaceFileWriter.Save(destination, merged);

        return (WriteOutcome.Updated, string.Create(
            CultureInfo.InvariantCulture,
            $"{WorkspaceTasks.NotePrefix}wrote {destination} (updated: {(index < 0 ? "added" : "replaced")} the task '{WorkspaceTasks.Label}')"));
    }

    private static string Filed(string destination) => string.Create(
        CultureInfo.InvariantCulture,
        $"{WorkspaceTasks.NotePrefix}wrote {destination} (created: added the task '{WorkspaceTasks.Label}')");

    /// <summary>
    /// Reads the task list as a document, refusing anything this command could not leave whole.
    /// </summary>
    /// <param name="text">The file's contents.</param>
    /// <param name="destination">The file, for the refusal.</param>
    /// <returns>The document to merge into; a blank file is an empty document.</returns>
    /// <exception cref="WorkspaceWriteException">The text is not JSON, or is not a JSON object.</exception>
    /// <remarks>
    /// The comment dialect is the refusal worth naming. VS Code accepts comments in <c>tasks.json</c>,
    /// <see cref="JsonNode"/> does not, and a reader for that dialect would be a second parser rather than a merge — so a
    /// file carrying one is refused at exit 7, untouched, with the message saying the task can be added by hand.
    /// </remarks>
    private static JsonObject Parse(string text, string destination)
    {
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
                var other => throw new WorkspaceWriteException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{destination} holds a JSON {other.GetValueKind()}, not an object; nothing was written.")),
            };
        }
        catch (JsonException exception)
        {
            throw new WorkspaceWriteException(string.Create(
                CultureInfo.InvariantCulture,
                $"{destination} is not valid JSON, so nothing was written: {exception.Message}"));
        }
    }

    /// <summary>
    /// Finds the task array, creating it when the document has none.
    /// </summary>
    /// <param name="document">The file.</param>
    /// <param name="destination">The file, for the refusal.</param>
    /// <returns>The array to file the task into.</returns>
    /// <exception cref="WorkspaceWriteException"><c>tasks</c> holds something that is not an array.</exception>
    private static JsonArray Tasks(JsonObject document, string destination)
    {
        switch (document["tasks"])
        {
            case JsonArray tasks:
                return tasks;

            case { } other:
                throw new WorkspaceWriteException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{WorkspaceTasks.Label}' could not be filed: 'tasks' in {destination} holds a JSON {other.GetValueKind()}, not an array; nothing was written."));

            default:
                var created = new JsonArray();
                document["tasks"] = created;
                return created;
        }
    }

    /// <summary>
    /// Finds the task with this label.
    /// </summary>
    /// <param name="tasks">The array.</param>
    /// <param name="label">The label to look for, matched ordinally.</param>
    /// <returns>Its index, or <c>-1</c> when the list holds no such task.</returns>
    /// <remarks>
    /// Scanned rather than looked up: a task list is a handful of entries, and the label is the only key the format gives
    /// this command to own an entry by.
    /// </remarks>
    private static int IndexOf(JsonArray tasks, string label)
    {
        for (var index = 0; index < tasks.Count; index++)
        {
            if (tasks[index] is JsonObject entry
                && entry["label"] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && string.Equals(text, label, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
