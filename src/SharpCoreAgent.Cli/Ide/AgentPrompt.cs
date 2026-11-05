using System.Globalization;

namespace SharpCoreAgent.Cli;

/// <summary>
/// The reusable prompt file <c>ide prompt</c> prints.
/// </summary>
/// <remarks>
/// A prompt file is the same instructions as the agent profile, but it is selected explicitly in the prompt picker
/// rather than being an always-on agent. The YAML frontmatter names the mode and description; the body is the profile's
/// instruction body, without the agent-only <c>tools</c> block.
/// </remarks>
internal static class AgentPrompt
{
    /// <summary>The file's name.</summary>
    internal const string FileName = "sharpcoreagent.prompt.md";

    /// <summary>The prefix every line <c>ide prompt</c> writes to stderr begins with.</summary>
    internal const string NotePrefix = "sharpcoreagent ide prompt: ";

    /// <summary>The line that says this file is this command's to replace.</summary>
    internal const string Marker =
        "<!-- written by 'sharpcoreagent ide prompt --write'; the next --write replaces this file whole, so edit a copy instead -->";

    /// <summary>Names the file the prompt belongs in.</summary>
    /// <param name="workspaceRoot">The workspace, absolute.</param>
    /// <returns>An absolute path inside it.</returns>
    internal static string Destination(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".github", "prompts", FileName);

    /// <summary>Renders the prompt.</summary>
    /// <returns>The document, newline-terminated.</returns>
    internal static string Render()
    {
        var profile = AgentProfile.Render().Replace("\r\n", "\n", StringComparison.Ordinal);
        var close = profile.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        var body = close < 0 ? profile : profile[(close + 5)..];
        var marker = body.LastIndexOf(AgentProfile.Marker, StringComparison.Ordinal);
        if (marker >= 0)
        {
            body = body[..marker];
        }

        return string.Join(
            Environment.NewLine,
            "---",
            "mode: agent",
            "description: Answers questions about this workspace from its code index, through the sharpcoreagent MCP server.",
            "---",
            string.Empty,
            body.TrimEnd(),
            string.Empty,
            Marker,
            string.Empty);
    }

    /// <summary>Reports whether a file is one this command wrote.</summary>
    /// <param name="text">The file's contents.</param>
    /// <returns><see langword="true"/> when the marker is an exact line.</returns>
    internal static bool IsOurs(string text) => text
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n')
        .Any(line => string.Equals(line.TrimEnd(), Marker, StringComparison.Ordinal));
}
