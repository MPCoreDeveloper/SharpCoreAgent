using System.Globalization;
using SharpCoreAgent.Domain.Code;

namespace SharpCoreAgent.Infrastructure.Code;

/// <summary>
/// The two ways the router itself cannot answer, as opposed to a language adapter declining.
/// </summary>
/// <remarks>
/// An adapter owns its own codes — <c>FSH001</c> and its siblings — because only it knows why the compiler could not
/// speak. These are the answers that are the same whatever the language is, and keeping them here stops two tools
/// from spelling "nothing claims this file" differently.
/// <para>
/// Both codes exist to be diagnosed by a developer rather than worked around by the model: <c>LANG001</c> is a
/// workspace the agent was not told how to read, and <c>LANG002</c> is a registration mistake in this repository.
/// Neither can happen for F# while the composition root registers all three ports against one adapter, and a test
/// proves that.
/// </para>
/// </remarks>
internal static class CodeFailures
{
    /// <summary>No registered adapter claims the file's extension.</summary>
    /// <param name="path">The path that was asked about.</param>
    /// <returns>A failure naming the extension and the three tools that answer without a compiler.</returns>
    public static CodeFailure NotClaimed(string path)
    {
        var extension = Path.GetExtension(path);

        var message = extension.Length == 0
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"No code intelligence is registered for '{path}', because it has no file extension to claim.")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"No code intelligence is registered for {extension} files, so '{path}' cannot be checked.");

        return new CodeFailure
        {
            Code = "LANG001",
            Message = message,
            Hint = "Use find_references for a name-based search, search_code for the surrounding code, or dotnet_verify for compiler errors.",
        };
    }

    /// <summary>The adapter that claims the file does not implement the port the question needs.</summary>
    /// <param name="path">The path that was asked about.</param>
    /// <param name="port">The port the question needed, by name.</param>
    /// <returns>A failure that names the port, because only a registration mistake produces it.</returns>
    public static CodeFailure NotAnswered(string path, string port) => new()
    {
        Code = "LANG002",
        Message = string.Create(
            CultureInfo.InvariantCulture,
            $"The adapter registered for '{path}' does not implement {port}, so it cannot answer that question."),
        Hint = "Report this: every registered language adapter must implement diagnostics, uses and signatures.",
    };
}
