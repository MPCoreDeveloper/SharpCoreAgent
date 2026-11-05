namespace SharpCoreAgent.Cli;

/// <summary>
/// The reusable agent skill <c>ide skill</c> prints.
/// </summary>
/// <remarks>
/// A skill is a named, discoverable bundle of instructions under <c>.github/skills/&lt;name&gt;/SKILL.md</c>. It carries
/// YAML <c>name</c> and <c>description</c> and the same instruction body as the agent profile.
/// </remarks>
internal static class AgentSkill
{
    /// <summary>The directory and file name.</summary>
    internal const string FileName = "SKILL.md";

    /// <summary>The skill name, also the directory name.</summary>
    internal const string Name = "sharpcoreagent";

    /// <summary>The prefix every line <c>ide skill</c> writes to stderr begins with.</summary>
    internal const string NotePrefix = "sharpcoreagent ide skill: ";

    /// <summary>The line that says this file is this command's to replace.</summary>
    internal const string Marker =
        "<!-- written by 'sharpcoreagent ide skill --write'; the next --write replaces this file whole, so edit a copy instead -->";

    /// <summary>Names the file the skill belongs in.</summary>
    /// <param name="workspaceRoot">The workspace, absolute.</param>
    /// <returns>An absolute path inside it.</returns>
    internal static string Destination(string workspaceRoot) =>
        Path.Combine(workspaceRoot, ".github", "skills", Name, FileName);

    /// <summary>Renders the skill.</summary>
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
            "---",
            $"name: {Name}",
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
