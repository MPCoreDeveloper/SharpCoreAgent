using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpCoreAgent.Cli;

/// <summary>
/// The task <c>ide tasks</c> prints: one entry in <c>.vscode/tasks.json</c> that rebuilds this workspace's index.
/// </summary>
/// <remarks>
/// The keys are the five the tasks page shows — <c>version</c>, <c>tasks</c>, <c>label</c>, <c>command</c>,
/// <c>args</c> — and nothing else. <c>group</c>, <c>problemMatcher</c> and <c>type</c> are described only in prose the
/// round did not read to the end, and a guessed <c>problemMatcher</c> is a file the editor rejects, which is worse than
/// a task with no matcher: without one it is a shell command that runs.
/// <para>
/// The command is this binary's absolute path, from the same <see cref="McpClientSnippets.Invocation"/> answer the MCP
/// blocks use, so the task runs the build that wrote the file. The cost is real and named: a committed
/// <c>tasks.json</c> then carries one machine's path, exactly as a committed <c>.mcp.json</c> already does.
/// </para>
/// </remarks>
internal static class WorkspaceTasks
{
    /// <summary>The label the task is filed under, and the key <c>--write</c> replaces by.</summary>
    /// <remarks>
    /// <c>server: name</c> is the editor's own grouping convention, so this appears under <c>sharpcoreagent</c> beside
    /// whatever else the workspace runs.
    /// </remarks>
    internal const string Label = "sharpcoreagent: index";

    /// <summary>The prefix every line <c>ide tasks</c> writes to stderr begins with.</summary>
    internal const string NotePrefix = "sharpcoreagent ide tasks: ";

    /// <summary>The document format every <c>tasks.json</c> in the wild carries.</summary>
    internal const string FormatVersion = "2.0.0";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Names the file the task belongs in.
    /// </summary>
    /// <param name="workspaceRoot">The workspace, absolute.</param>
    /// <returns>An absolute path inside it.</returns>
    internal static string Destination(string workspaceRoot) => Path.Combine(workspaceRoot, ".vscode", "tasks.json");

    /// <summary>
    /// Builds the one task this command owns.
    /// </summary>
    /// <returns>A detached entry, ready to be filed into a document.</returns>
    internal static JsonObject Task()
    {
        var (command, arguments) = McpClientSnippets.Invocation("index");

        var args = new JsonArray();
        foreach (var argument in arguments)
        {
            args.Add(JsonValue.Create(argument));
        }

        return new JsonObject
        {
            ["label"] = Label,
            ["command"] = command,
            ["args"] = args,
        };
    }

    /// <summary>
    /// Builds the whole document, as it is printed and as it is written into a file that had none.
    /// </summary>
    /// <returns>The document.</returns>
    internal static JsonObject Document() => new()
    {
        ["version"] = FormatVersion,
        ["tasks"] = new JsonArray(Task()),
    };

    /// <summary>
    /// Renders the document the way a printed one is read.
    /// </summary>
    /// <returns>The document, newline-terminated.</returns>
    internal static string Render() => Document().ToJsonString(Indented) + Environment.NewLine;
}
