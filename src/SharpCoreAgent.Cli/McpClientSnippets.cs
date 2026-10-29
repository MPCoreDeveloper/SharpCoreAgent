using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpCoreAgent.Cli;

/// <summary>
/// The three configuration blocks <c>mcp config</c> prints, and the file each one belongs in.
/// </summary>
/// <remarks>
/// Three agents, three file shapes, and the difference is not cosmetic: Claude Code reads an <c>mcpServers</c>
/// object from <c>.mcp.json</c>, VS Code reads a <c>servers</c> object from <c>.vscode/mcp.json</c> and insists on a
/// <c>type</c>, and Codex reads a TOML table from <c>config.toml</c>. A developer who has to learn that from three
/// blog posts is the developer this command exists for.
/// <para>
/// The blocks are built, not formatted by hand: the JSON through <see cref="JsonNode"/> so a Windows path arrives
/// escaped, and the TOML with a literal string, which is the one TOML form in which a backslash stays a backslash.
/// </para>
/// </remarks>
internal static class McpClientSnippets
{
    /// <summary>The name the entry is filed under, and the prefix the agent puts in front of every tool name.</summary>
    /// <remarks>
    /// <c>sharpcoreagent</c>, because that is what the server answers in <c>serverInfo</c> and what FR-017 expects the
    /// tools to be called (<c>sharpcoreagent_search_code</c>). A client showing a different name beside a different
    /// server name would be one more thing to explain.
    /// </remarks>
    internal const string ServerName = "sharpcoreagent";

    /// <summary>The arguments every snippet carries: the command, started as the server.</summary>
    private static readonly string[] Serve = ["mcp", "serve"];

    /// <summary>
    /// Renders the block for one client.
    /// </summary>
    /// <param name="client">Whose configuration the block is for.</param>
    /// <param name="command">The absolute path of the executable the client should start.</param>
    /// <param name="arguments">What to pass it.</param>
    /// <returns>The block, newline-terminated, ready to paste or redirect into the file.</returns>
    internal static string Render(McpClientKind client, string command, IReadOnlyList<string> arguments) => client switch
    {
        McpClientKind.Claude => Json("mcpServers", typed: false, command, arguments),
        McpClientKind.VsCode => Json("servers", typed: true, command, arguments),
        _ => Toml(command, arguments),
    };

    /// <summary>
    /// Names the file the block belongs in.
    /// </summary>
    /// <param name="client">Whose configuration the block is for.</param>
    /// <param name="workspaceRoot">The workspace, absolute: two of the three files are found from it.</param>
    /// <returns>An absolute path for the user-scope file, a path inside the workspace for the two project ones.</returns>
    internal static string Destination(McpClientKind client, string workspaceRoot) => client switch
    {
        McpClientKind.Claude => Path.Combine(workspaceRoot, ".mcp.json"),
        McpClientKind.VsCode => Path.Combine(workspaceRoot, ".vscode", "mcp.json"),
        _ => Path.Combine(UserProfile(), ".codex", "config.toml"),
    };

    /// <summary>
    /// Says where the block goes, in the one line that goes to stderr.
    /// </summary>
    /// <param name="client">Whose configuration the block is for.</param>
    /// <param name="destination">The file, from <see cref="Destination"/>.</param>
    /// <returns>One line naming the file and the scope it is read in.</returns>
    /// <remarks>
    /// On stderr because stdout is the block itself and nothing else: <c>mcp config --client claude &gt; .mcp.json</c>
    /// is the intended use, and a sentence inside the JSON would make the file unreadable.
    /// </remarks>
    internal static string Note(McpClientKind client, string destination) => client switch
    {
        McpClientKind.Claude =>
            $"sharpcoreagent mcp config: paste into {destination} (project scope: committed, and shared with the team)",
        McpClientKind.VsCode =>
            $"sharpcoreagent mcp config: paste into {destination} (workspace scope: VS Code reads .vscode/mcp.json)",
        _ =>
            $"sharpcoreagent mcp config: append to {destination} (user scope: one table per server, beside the rest)",
    };

    /// <summary>
    /// Works out how this binary has to be started, so a snippet names something that will run.
    /// </summary>
    /// <returns>The command, and the arguments before <c>mcp serve</c> when the command is the .NET host.</returns>
    /// <remarks>
    /// <see cref="Environment.ProcessPath"/> is the executable the shell started, which is the apphost in a normal
    /// install. Under a debugger, a test host or a hand-written <c>dotnet sharpcoreagent.dll</c> it is <c>dotnet</c>
    /// itself, and then the executable alone answers nothing: the entry assembly has to be named too, or the snippet
    /// starts the runtime with no program to run.
    /// </remarks>
    internal static (string Command, IReadOnlyList<string> Arguments) Invocation()
    {
        var process = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(process))
        {
            // The platform will not say: the name on PATH is the best guess, and the one the README uses.
            return (ServerName, Serve);
        }

        var command = Path.GetFullPath(process);
        return IsDotNetHost(command) && EntryAssembly() is { } assembly
            ? (command, [assembly, .. Serve])
            : (command, Serve);
    }

    /// <summary>Renders a JSON block for one client, indented because a human pastes it.</summary>
    /// <param name="root">The top-level key that client reads: <c>mcpServers</c> or <c>servers</c>.</param>
    /// <param name="typed">Whether that client requires an explicit <c>type</c> on a stdio entry.</param>
    /// <param name="command">The executable.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>The document, newline-terminated.</returns>
    private static string Json(string root, bool typed, string command, IReadOnlyList<string> arguments)
    {
        var entry = new JsonObject();
        if (typed)
        {
            entry["type"] = "stdio";
        }

        entry["command"] = command;

        var args = new JsonArray();
        foreach (var argument in arguments)
        {
            args.Add(JsonValue.Create(argument));
        }

        entry["args"] = args;

        var document = new JsonObject { [root] = new JsonObject { [ServerName] = entry } };
        return document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
    }

    /// <summary>Renders the Codex table.</summary>
    /// <param name="command">The executable.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>The table, newline-terminated.</returns>
    private static string Toml(string command, IReadOnlyList<string> arguments) => string.Concat(
        $"[mcp_servers.{ServerName}]",
        Environment.NewLine,
        $"command = {TomlString(command)}",
        Environment.NewLine,
        $"args = [{string.Join(", ", arguments.Select(TomlString))}]",
        Environment.NewLine);

    /// <summary>
    /// Writes one value as a TOML string.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>A literal string, or a basic one when a literal cannot carry the value.</returns>
    /// <remarks>
    /// A literal string (<c>'…'</c>) is the right form for a Windows path: TOML gives a backslash no meaning inside
    /// one, so <c>C:\tools\sharpcoreagent.exe</c> survives unchanged. A value holding a single quote or a line break
    /// falls back to a basic string, where a backslash and a quote are escaped.
    /// </remarks>
    private static string TomlString(string value) => value.AsSpan().IndexOfAny('\'', '\n', '\r') < 0
        ? string.Concat("'", value, "'")
        : string.Concat(
            "\"",
            value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal),
            "\"");

    /// <summary>
    /// Reports whether the executable is the .NET host rather than this program.
    /// </summary>
    /// <param name="path">The process's own path.</param>
    /// <returns><see langword="true"/> for <c>dotnet</c> and <c>dotnet.exe</c>.</returns>
    private static bool IsDotNetHost(string path) =>
        string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds the assembly a client should load when the executable is the host.</summary>
    /// <returns>The absolute path, or <see langword="null"/> in a single-file publish, where there is none.</returns>
    private static string? EntryAssembly()
    {
        var location = Assembly.GetEntryAssembly()?.Location;
        return string.IsNullOrWhiteSpace(location) ? null : location;
    }

    /// <summary>Names the user's home directory, for the Codex file only.</summary>
    /// <returns>An absolute path, or <c>~</c> when the platform will not say.</returns>
    private static string UserProfile()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? "~" : profile;
    }
}
