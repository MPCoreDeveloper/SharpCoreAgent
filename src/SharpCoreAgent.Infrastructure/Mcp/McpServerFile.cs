using System.Text;

namespace SharpCoreAgent.Infrastructure.Mcp;

/// <summary>
/// One server a declarative file named: a display/prefix name, one command line, and its environment additions.
/// </summary>
/// <param name="Name">The prefix the file gave the server.</param>
/// <param name="CommandLine">The command line the file's command and arguments produce.</param>
/// <param name="Environment">Additional environment values for the child, never replacing the parent's.</param>
public sealed record McpServerDefinition(
    string Name,
    string CommandLine,
    IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>Gets an empty environment, used by a file entry that names none.</summary>
    public static IReadOnlyDictionary<string, string> NoEnvironment { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Reads a declarative server list only when a developer names the file explicitly.
/// </summary>
/// <remarks>
/// The accepted shape is the two keys the MCP ecosystem already uses: <c>mcpServers</c> and <c>servers</c>. Each
/// entry must be a local <c>command</c> with optional <c>args</c> and <c>env</c>. A <c>url</c> entry is refused here
/// because remote endpoints require the explicit transport and OAuth flags rather than a file silently starting a
/// socket. Reading this file never happens as a default; the caller's <c>--mcp-file</c> is the authority.
/// </remarks>
public static class McpServerFile
{
    private const int MaxBytes = 1024 * 1024;

    /// <summary>Loads and validates a server list.</summary>
    /// <param name="path">The file path named on the command line.</param>
    /// <returns>The definitions, in file order.</returns>
    /// <exception cref="ArgumentException">The path is blank, the file is too large, or an entry is invalid.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="System.Text.Json.JsonException">The file is not valid JSON.</exception>
    public static IReadOnlyList<McpServerDefinition> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            throw new ArgumentException($"the MCP server file '{path}' does not exist", nameof(path));
        }

        if (info.Length > MaxBytes)
        {
            throw new ArgumentException($"the MCP server file '{path}' is larger than 1 MiB", nameof(path));
        }

        return Parse(File.ReadAllText(full), path);
    }

    /// <summary>Parses a server list from text, for tests and embedders.</summary>
    /// <param name="json">The JSON document.</param>
    /// <param name="source">The file name used in errors.</param>
    /// <returns>The definitions, in file order.</returns>
    /// <exception cref="ArgumentException">The document has no supported server object or an entry is invalid.</exception>
    /// <exception cref="System.Text.Json.JsonException">The text is not valid JSON.</exception>
    public static IReadOnlyList<McpServerDefinition> Parse(string json, string source = "mcp file")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !(TryMap(root, "mcpServers", out var servers) || TryMap(root, "servers", out servers)))
        {
            throw new ArgumentException($"'{source}' must contain an object named 'mcpServers' or 'servers'");
        }

        var definitions = new List<McpServerDefinition>();
        foreach (var entry in servers.EnumerateObject())
        {
            var name = entry.Name.Trim();
            if (name.Length == 0)
            {
                throw new ArgumentException($"'{source}' contains a server with no name");
            }

            if (entry.Value.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                throw new ArgumentException($"'{source}' server '{name}' must be an object");
            }

            if (entry.Value.TryGetProperty("url", out _))
            {
                throw new ArgumentException(
                    $"'{source}' server '{name}' is remote; use --mcp-url or --mcp-sse-url instead of a file entry");
            }

            var command = ReadString(entry.Value, "command");
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new ArgumentException($"'{source}' server '{name}' needs a non-empty command");
            }

            var args = ReadArgs(entry.Value, source, name);
            var environment = ReadEnvironment(entry.Value, source, name);
            definitions.Add(new McpServerDefinition(name, CommandLine(command, args), environment));
        }

        if (definitions.Count == 0)
        {
            throw new ArgumentException($"'{source}' names no servers");
        }

        return definitions;
    }
 
    private static bool TryMap(
        System.Text.Json.JsonElement root,
        string name,
        out System.Text.Json.JsonElement value) =>
        root.TryGetProperty(name, out value) && value.ValueKind == System.Text.Json.JsonValueKind.Object;

    private static string? ReadString(System.Text.Json.JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<string> ReadArgs(
        System.Text.Json.JsonElement entry,
        string source,
        string name)
    {
        if (!entry.TryGetProperty("args", out var args) || args.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            return [];
        }

        if (args.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            throw new ArgumentException($"'{source}' server '{name}' args must be an array of strings");
        }

        var values = new List<string>();
        foreach (var arg in args.EnumerateArray())
        {
            if (arg.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                throw new ArgumentException($"'{source}' server '{name}' args must be an array of strings");
            }

            values.Add(arg.GetString() ?? string.Empty);
        }

        return values;
    }

    private static IReadOnlyDictionary<string, string> ReadEnvironment(
        System.Text.Json.JsonElement entry,
        string source,
        string name)
    {
        if (!entry.TryGetProperty("env", out var env) || env.ValueKind == System.Text.Json.JsonValueKind.Null)
        {
            return McpServerDefinition.NoEnvironment;
        }

        if (env.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new ArgumentException($"'{source}' server '{name}' env must be an object of strings");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in env.EnumerateObject())
        {
            if (variable.Value.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                throw new ArgumentException($"'{source}' server '{name}' env '{variable.Name}' must be a string");
            }

            values[variable.Name] = variable.Value.GetString() ?? string.Empty;
        }

        return values;
    }

    private static string CommandLine(string command, IReadOnlyList<string> args)
    {
        var text = new StringBuilder(command);
        foreach (var arg in args)
        {
            text.Append(' ').Append(Quote(arg));
        }

        return text.ToString();
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.All(c => !char.IsWhiteSpace(c) && c != '"' && c != '&' && c != '|' && c != '<' && c != '>'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}

