namespace SharpCoreAgent.Domain.Code;

/// <summary>
/// Answers "what does the compiler say about this file?" for one language, without building the workspace.
/// </summary>
/// <remarks>
/// Read-only by construction: there is no method here that writes, formats or fixes, so a language service
/// reached through this port cannot become a second way to change a file.
/// <para>
/// Implementations never throw for absence. A missing project, an unresolvable reference set or an expired budget
/// is a <see cref="CodeFailure"/> with a code the tool renders and a hint naming a tool this repository ships. The
/// only exception an implementation may let through is <see cref="OperationCanceledException"/> from the token,
/// because a cancelled turn is not an answer.
/// </para>
/// </remarks>
public interface ICodeDiagnostics
{
    /// <summary>
    /// Checks one file and returns what the language service reported for it.
    /// </summary>
    /// <param name="path">A workspace-relative path to a source file this language claims.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>
    /// The diagnostics ordered by line, then column; an empty list when the file is clean; a failure when the check
    /// could not run.
    /// </returns>
    ValueTask<CodeDiagnosticsResult> GetDiagnosticsAsync(string path, CancellationToken cancellationToken);
}
