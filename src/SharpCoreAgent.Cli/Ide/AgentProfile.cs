using System.Globalization;
using SharpCoreAgent.Infrastructure.McpServer;

namespace SharpCoreAgent.Cli;

/// <summary>
/// The agent profile <c>ide agent</c> prints: the file that puts this workspace's index inside an editor's agent picker.
/// </summary>
/// <remarks>
/// Two keys and a prompt. The format is Microsoft's, read on 2026-11-02 and quoted in the round's <c>plan.md</c>:
/// <c>.github/agents/&lt;name&gt;.agent.md</c>, a YAML frontmatter block, <c>description</c> required, <c>tools</c> a list of
/// names where an MCP tool is <c>&lt;server&gt;/&lt;tool&gt;</c>, and a prompt capped at 30,000 characters.
/// <para>
/// The tool list is built from <see cref="McpServerCatalog.PublishedToolNames"/> rather than
/// written out here, because a profile naming a tool the server stopped publishing is a picker that offers a missing
/// tool at the moment a developer asks a question — and neither file would say which of the two is wrong.
/// </para>
/// <para>
/// The last line is a marker. It is not decoration: <see cref="IdeFileWriter"/> replaces the file whole only when it is
/// there, so a profile somebody wrote by hand is refused rather than overwritten.
/// </para>
/// </remarks>
internal static class AgentProfile
{
    /// <summary>The file's name, which is also the name the picker shows: without a <c>name</c> key it defaults to this.</summary>
    internal const string FileName = "sharpcoreagent.agent.md";

    /// <summary>The prefix every line <c>ide agent</c> writes to stderr begins with.</summary>
    internal const string NotePrefix = "sharpcoreagent ide agent: ";

    /// <summary>The line that says this file is this command's to replace.</summary>
    internal const string Marker =
        "<!-- written by 'sharpcoreagent ide agent --write'; the next --write replaces this file whole, so edit a copy instead -->";

    /// <summary>
    /// Names the file the profile belongs in.
    /// </summary>
    /// <param name="workspaceRoot">The workspace, absolute.</param>
    /// <returns>An absolute path inside it.</returns>
    internal static string Destination(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".github", "agents", FileName);

    /// <summary>
    /// Renders the profile.
    /// </summary>
    /// <returns>The document, newline-terminated, ready to redirect or to write.</returns>
    /// <remarks>
    /// Assembled line by line rather than as one raw string literal, so the line ending is the platform's and a test can
    /// compare the text it wrote with the text it read.
    /// </remarks>
    internal static string Render()
    {
        var tools = string.Join(
            ", ",
            McpServerCatalog.PublishedToolNames.Select(
                name => string.Create(CultureInfo.InvariantCulture, $"\"{McpClientSnippets.ServerName}/{name}\"")));

        string[] lines =
        [
            "---",
            "name: sharpcoreagent",
            "description: Answers questions about this workspace from its code index, through the sharpcoreagent MCP server.",
            "mcp-servers: [sharpcoreagent]",
            string.Create(CultureInfo.InvariantCulture, $"tools: [{tools}]"),
            "---",
            string.Empty,
            "You answer questions about this workspace from its code index, and you say where each answer came from.",
            string.Empty,
            "Reach for the index before you read a file:",
            string.Empty,
            "- `sharpcoreagent/search_code` finds ranked passages for a question and prints them as `path:start-end`.",
            "- `sharpcoreagent/find_symbol` finds a declaration when you already know its name.",
            "- `sharpcoreagent/find_references` walks from a declaration to its callers or to its callees.",
            "- `sharpcoreagent/code_context` explains what sits around a declaration: its typed neighbours, and the",
            "  passage it is declared in.",
            "- `sharpcoreagent/index_status` says whether the index is fresh, stale, partial or missing, and how big it is.",
            string.Empty,
            "Rules:",
            string.Empty,
            "- Quote the path and the line numbers a tool returned. Never invent a file, a line or a count.",
            "- All five tools are read-only. Nothing you do in this session changes the workspace, so do not say that it",
            "  did.",
            "- If `index_status` says `missing`, say so and suggest `sharpcoreagent index`. If it says `stale` or",
            "  `partial`, put that beside the answer rather than in a footnote.",
            "- The index holds text and structure, not intent. When the question is about design, say what the code does",
            "  and where.",
            string.Empty,
            Marker,
        ];

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>
    /// Reports whether a file already on disk is one this command may replace.
    /// </summary>
    /// <param name="text">The file's contents, as they were read.</param>
    /// <returns><see langword="true"/> when the marker is a line of its own.</returns>
    /// <remarks>
    /// A contained marker would be enough for a file this command wrote, but an exact line is what a developer sees when
    /// they open it — and deleting that line is how they say "this one is mine now".
    /// </remarks>
    internal static bool IsOurs(string text) => text
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n')
        .Any(line => string.Equals(line.TrimEnd(), Marker, StringComparison.Ordinal));
}
