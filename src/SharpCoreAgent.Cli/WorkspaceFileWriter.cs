using System.Text;

namespace SharpCoreAgent.Cli;

/// <summary>What a <c>--write</c> did to the file it owns.</summary>
internal enum WriteOutcome
{
    /// <summary>The file did not exist, and now does.</summary>
    Created,

    /// <summary>The file existed and held something this command owns, and that changed.</summary>
    Updated,

    /// <summary>The file already said exactly this, so it was not touched.</summary>
    Unchanged,
}

/// <summary>
/// Reads and writes the files a <c>--write</c> touches: a client's MCP configuration, an editor's agent profile, a
/// workspace's task list.
/// </summary>
/// <remarks>
/// Three commands, three merge rules, one way of getting bytes on and off a disk. The rules stay with the command that
/// owns them — a key in an object, a file whose owner is marked, a task found by label — because that is where the
/// argument for each one belongs; what is shared is the part with no opinion: read the text, or write it without a
/// byte-order mark, creating the directory when it is missing, and turn an OS failure into one message naming the file.
/// <para>
/// No temporary file and no rename. Every caller here writes a file whose loss costs a re-run of a command with no side
/// effect, and a temporary file beside a developer's <c>.mcp.json</c> or <c>tasks.json</c> is a file their editor may try
/// to read.
/// </para>
/// </remarks>
internal static class WorkspaceFileWriter
{
    /// <summary>
    /// Reads a file this command may have written before.
    /// </summary>
    /// <param name="destination">The file, which exists.</param>
    /// <returns>Its text, exactly as it was.</returns>
    /// <exception cref="WorkspaceWriteException">The file exists and could not be read.</exception>
    internal static string ReadText(string destination)
    {
        try
        {
            return File.ReadAllText(destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceWriteException($"{destination} could not be read: {exception.Message}");
        }
    }

    /// <summary>
    /// Writes a document in one pass, with no byte-order mark and whatever line endings the text already carries.
    /// </summary>
    /// <param name="destination">The file, which may not exist yet.</param>
    /// <param name="text">The whole document.</param>
    /// <exception cref="WorkspaceWriteException">The directory or the file could not be written.</exception>
    /// <remarks>
    /// The directory is created when it is missing: <c>.vscode</c> and <c>.github/agents</c> are the common case for a
    /// repository that has never had one, and the command's job is that the next step is not "make the folder first".
    /// The write is a single <see cref="File.WriteAllBytes(string, byte[])"/>, which is the failure mode that matters —
    /// never half a document.
    /// </remarks>
    internal static void Save(string destination, string text)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);

        try
        {
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(destination, bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new WorkspaceWriteException($"{destination} could not be written: {exception.Message}");
        }
    }
}

/// <summary>
/// The failure a <c>--write</c> reports when a file it owns could not be read, merged or written.
/// </summary>
/// <remarks>
/// A distinct type because the exit code is distinct: the command line was understood, so this is not a usage error —
/// it is a file in the workspace, and <see cref="ExitCodes.Configuration"/> is the code a script branches on. Raised
/// before anything is written, so the file the developer opens afterwards is the file they had.
/// </remarks>
public sealed class WorkspaceWriteException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="WorkspaceWriteException"/> class.</summary>
    /// <param name="message">What could not be done, naming the file.</param>
    public WorkspaceWriteException(string message)
        : base(message)
    {
    }
}
