using System.Globalization;
using System.Text;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Implements <c>sharpcoreagent ide</c>: the two files that put this workspace's index inside an editor.
/// </summary>
/// <remarks>
/// Both actions answer a question about files. No index is opened, no settings document is read, no container is built,
/// no credential is looked for, no socket is bound and no process is started — a developer wiring up an editor should not
/// need an index to exist first, and a command that wrote a file must not be a command that could also do something else.
/// <para>
/// Printing is the default for the same reason <c>mcp config</c> prints: the bytes are the answer, and a redirect is a
/// shape a developer already knows. <c>--write</c> is the other half, because a redirect truncates and these two files
/// belong to documents that may already hold somebody else's keys.
/// </para>
/// </remarks>
internal static class IdeCommand
{
    /// <summary>
    /// Runs the action the command line asked for.
    /// </summary>
    /// <param name="options">The parsed command line: the action, the write switch and the workspace.</param>
    /// <param name="output">Where a printed document goes, and nothing else — or nothing at all under <c>--write</c>.</param>
    /// <param name="error">Where the one line naming the file goes.</param>
    /// <param name="cancellationToken">Cancels the write, which only happens if the reader stopped reading.</param>
    /// <returns>The process exit code: 0 for a document printed or a file written, 7 when a file could not be read or merged.</returns>
    internal static async Task<int> ExecuteAsync(
        CliOptions options,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        return options.Ide switch
        {
            IdeAction.Tasks => await TasksAsync(options, output, error, cancellationToken).ConfigureAwait(false),
            IdeAction.Prompt => await PromptAsync(options, output, error, cancellationToken).ConfigureAwait(false),
            IdeAction.Instructions => await InstructionsAsync(options, output, error, cancellationToken).ConfigureAwait(false),
            IdeAction.Skill => await SkillAsync(options, output, error, cancellationToken).ConfigureAwait(false),
            _ => await AgentAsync(options, output, error, cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// Prints or writes the agent profile.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="output">Where the profile goes.</param>
    /// <param name="error">Where the note goes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> AgentAsync(
        CliOptions options,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var destination = AgentProfile.Destination(Path.GetFullPath(options.Workspace));

        if (options.Write)
        {
            return await WriteAsync(
                AgentProfile.NotePrefix,
                () => IdeFileWriter.WriteProfile(destination, AgentProfile.Render(), options.Force),
                error,
                cancellationToken).ConfigureAwait(false);
        }

        return await PrintAsync(
            AgentProfile.Render(),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{AgentProfile.NotePrefix}{destination} (workspace scope: VS Code and the Copilot CLI read .github/agents; --write writes it there)"),
            output,
            error,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Prints or writes the reusable prompt.
    /// </summary>
    private static async Task<int> PromptAsync(
        CliOptions options,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var destination = AgentPrompt.Destination(Path.GetFullPath(options.Workspace));

        if (options.Write)
        {
            return await WriteAsync(
                AgentPrompt.NotePrefix,
                () => IdeFileWriter.WritePrompt(destination, AgentPrompt.Render(), options.Force),
                error,
                cancellationToken).ConfigureAwait(false);
        }

        return await PrintAsync(
            AgentPrompt.Render(),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{AgentPrompt.NotePrefix}{destination} (VS Code reads .github/prompts; --write writes it there)"),
            output,
            error,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Prints or writes the repository-wide instructions.
    /// </summary>
    private static async Task<int> InstructionsAsync(
        CliOptions options,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var destination = AgentInstructions.Destination(Path.GetFullPath(options.Workspace));

        if (options.Write)
        {
            return await WriteAsync(
                AgentInstructions.NotePrefix,
                () => IdeFileWriter.WriteInstructions(destination, AgentInstructions.Render(), options.Force),
                error,
                cancellationToken).ConfigureAwait(false);
        }

        return await PrintAsync(
            AgentInstructions.Render(),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{AgentInstructions.NotePrefix}{destination} (VS Code/Copilot reads .github/copilot-instructions.md; --write writes it there)"),
            output,
            error,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Prints or writes the agent skill.</summary>
    private static async Task<int> SkillAsync(
        CliOptions options,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var destination = AgentSkill.Destination(Path.GetFullPath(options.Workspace));

        if (options.Write)
        {
            return await WriteAsync(
                AgentSkill.NotePrefix,
                () => IdeFileWriter.WriteSkill(destination, AgentSkill.Render(), options.Force),
                error,
                cancellationToken).ConfigureAwait(false);
        }

        return await PrintAsync(
            AgentSkill.Render(),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{AgentSkill.NotePrefix}{destination} (VS Code/Copilot reads .github/skills; --write writes it there)"),
            output,
            error,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Prints or writes the workspace task.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="output">Where the document goes.</param>
    /// <param name="error">Where the note goes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> TasksAsync(
        CliOptions options,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var destination = WorkspaceTasks.Destination(Path.GetFullPath(options.Workspace));

        if (options.Write)
        {
            return await WriteAsync(
                WorkspaceTasks.NotePrefix,
                () => IdeFileWriter.WriteTasks(destination, WorkspaceTasks.Task(), options.Force),
                error,
                cancellationToken).ConfigureAwait(false);
        }

        return await PrintAsync(
            WorkspaceTasks.Render(),
            string.Create(
                CultureInfo.InvariantCulture,
                $"{WorkspaceTasks.NotePrefix}{destination} (VS Code reads .vscode/tasks.json; --write merges this task into it)"),
            output,
            error,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the document to stdout and names the file it belongs in.
    /// </summary>
    /// <param name="document">The text to print.</param>
    /// <param name="note">The one line for stderr.</param>
    /// <param name="output">Where the document goes.</param>
    /// <param name="error">Where the note goes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see cref="ExitCodes.Success"/>.</returns>
    /// <remarks>
    /// No byte-order mark: the common case is a redirect into the file itself, and one in front of YAML frontmatter or a
    /// JSON object is a first character neither format wants.
    /// </remarks>
    private static async Task<int> PrintAsync(
        string document,
        string note,
        Stream output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        await output.WriteAsync(Encoding.UTF8.GetBytes(document), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        await error.WriteLineAsync(note.AsMemory(), cancellationToken).ConfigureAwait(false);

        return ExitCodes.Success;
    }

    /// <summary>
    /// Runs one of the two writers and reports what it did.
    /// </summary>
    /// <param name="prefix">The command's note prefix, for a refusal that has no line of its own.</param>
    /// <param name="write">The writer, which is the only thing that knows the merge rule.</param>
    /// <param name="error">Where the one line goes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The exit code: 0 when the file says what was asked, 7 when it could not.</returns>
    /// <remarks>
    /// Exit 7 and not 2: the command line was understood, and what needs a human is a file. The file is left exactly as it
    /// was, so the developer who reads the message and opens the path sees what this command saw.
    /// </remarks>
    private static async Task<int> WriteAsync(
        string prefix,
        Func<(WriteOutcome Outcome, string Note)> write,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            var (_, note) = write();

            await error.WriteLineAsync(note.AsMemory(), cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        }
        catch (WorkspaceWriteException exception)
        {
            await error
                .WriteLineAsync($"{prefix}{exception.Message}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);

            return ExitCodes.Configuration;
        }
    }
}
