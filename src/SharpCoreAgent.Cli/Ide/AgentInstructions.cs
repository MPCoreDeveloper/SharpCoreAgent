namespace SharpCoreAgent.Cli;

/// <summary>
/// The repository-wide instructions file <c>ide instructions</c> prints.
/// </summary>
/// <remarks>
/// Unlike a prompt, this file is not selected per request: it applies to every Copilot interaction in the repository.
/// It is therefore the agent body without frontmatter, followed by the ownership marker.
/// </remarks>
internal static class AgentInstructions
{
    /// <summary>The file's name.</summary>
    internal const string FileName = "copilot-instructions.md";

    /// <summary>The prefix every line <c>ide instructions</c> writes to stderr begins with.</summary>
    internal const string NotePrefix = "sharpcoreagent ide instructions: ";

    /// <summary>The line that says this file is this command's to replace.</summary>
    internal const string Marker =
        "<!-- written by 'sharpcoreagent ide instructions --write'; the next --write replaces this file whole, so edit a copy instead -->";

    /// <summary>Names the file the instructions belong in.</summary>
    /// <param name="workspaceRoot">The workspace, absolute.</param>
    /// <returns>An absolute path inside it.</returns>
    internal static string Destination(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".github", FileName);

    /// <summary>Renders the instructions.</summary>
    /// <returns>The document, newline-terminated.</returns>
    internal static string Render()
    {
        var prompt = AgentPrompt.Render().Replace("\r\n", "\n", StringComparison.Ordinal);
        var close = prompt.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        var body = close < 0 ? prompt : prompt[(close + 5)..];
        var marker = body.LastIndexOf(AgentPrompt.Marker, StringComparison.Ordinal);
        if (marker >= 0)
        {
            body = body[..marker];
        }

        return string.Join(
            Environment.NewLine,
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
