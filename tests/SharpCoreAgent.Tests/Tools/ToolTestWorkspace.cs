using System.Text.Json;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Conversation;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Tests.Tools;

/// <summary>
/// A throwaway workspace under the system temp directory, deleted when the test ends.
/// </summary>
/// <remarks>
/// Tools are only interesting when they touch a real file system: the escape checks resolve links, the
/// truncation limits count bytes and the ignore rules walk directories. Faking any of that would test
/// the fake, so every tool test gets a real directory and a real <see cref="Domain.Workspace"/>.
/// </remarks>
internal sealed class ToolTestWorkspace : IDisposable
{
    private readonly List<string> _attachmentRoots = [];

    private ToolTestWorkspace(string root)
    {
        Root = root;
        Workspace = Workspace.Create(root);
        Context = new ToolInvocationContext(Workspace);
    }

    /// <summary>Gets the absolute root directory.</summary>
    public string Root { get; }

    /// <summary>Gets the workspace the tools are confined to.</summary>
    public Workspace Workspace { get; private set; }

    /// <summary>Gets the context passed to every tool invocation.</summary>
    public ToolInvocationContext Context { get; private set; }

    /// <summary>Creates a new sandbox with a unique root.</summary>
    /// <returns>The sandbox, which the caller disposes.</returns>
    public static ToolTestWorkspace Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "sharpcoreagent-tools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new ToolTestWorkspace(root);
    }

    /// <summary>
    /// Creates a sibling directory and attaches it to this sandbox as a read-only root.
    /// </summary>
    /// <param name="name">The directory name; it is suffixed with a unique value.</param>
    /// <returns>The absolute path of the new root.</returns>
    /// <remarks>
    /// A sibling rather than a child on purpose: a root inside the primary root is dropped by
    /// <see cref="Workspace.WithAttachments(IEnumerable{string})"/>, so a child directory would make every
    /// attachment test pass for the wrong reason.
    /// </remarks>
    public string AddAttachment(string name)
    {
        var path = Path.Combine(Path.GetDirectoryName(Root)!, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _attachmentRoots.Add(path);
        Workspace = Workspace.Create(Root).WithAttachments(_attachmentRoots);
        Context = new ToolInvocationContext(Workspace);
        return path;
    }

    /// <summary>Writes a text file under any directory, creating its parent directory.</summary>
    /// <param name="directory">The absolute directory to write into.</param>
    /// <param name="relativePath">The path inside it, using either separator.</param>
    /// <param name="content">The file's contents.</param>
    /// <returns>The absolute path that was written.</returns>
    public static string WriteUnder(string directory, string relativePath, string content)
    {
        var full = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Writes a text file, creating its parent directory.</summary>
    /// <param name="relativePath">The path inside the sandbox, using either separator.</param>
    /// <param name="content">The file's contents.</param>
    /// <returns>The absolute path that was written.</returns>
    public string Write(string relativePath, string content)
    {
        var full = FullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Writes raw bytes, for the binary-file cases.</summary>
    /// <param name="relativePath">The path inside the sandbox.</param>
    /// <param name="bytes">The bytes to write.</param>
    /// <returns>The absolute path that was written.</returns>
    public string WriteBytes(string relativePath, byte[] bytes)
    {
        var full = FullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    /// <summary>Resolves a path inside the sandbox.</summary>
    /// <param name="relativePath">The path inside the sandbox.</param>
    /// <returns>The absolute path.</returns>
    public string FullPath(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Builds a tool call with a stable identifier.</summary>
    /// <param name="name">The tool name.</param>
    /// <param name="argumentsJson">The raw argument JSON.</param>
    /// <returns>The call.</returns>
    public static ToolCall Call(string name, string argumentsJson) => new("call_1", name, argumentsJson);

    /// <summary>
    /// Serialises an anonymous object into argument JSON, so paths need no manual escaping.
    /// </summary>
    /// <param name="arguments">The argument object.</param>
    /// <returns>The JSON the model would have produced.</returns>
    public static string Args(object arguments) => JsonSerializer.Serialize(arguments);

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var attachment in _attachmentRoots)
        {
            Delete(attachment);
        }

        Delete(Root);
    }

    private static void Delete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a passing test over.
        }
    }
}
