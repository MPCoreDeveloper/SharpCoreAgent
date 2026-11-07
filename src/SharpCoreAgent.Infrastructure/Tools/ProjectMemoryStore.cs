using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>The store behind <c>project_memory</c> and <c>remember</c>: bounded, slug-addressed notes.</summary>
/// <remarks>
/// Notes live under <c>${workspace}/.sharpcoreagent/memory/&lt;slug&gt;.md</c> — the git-ignored directory the transcript
/// already uses, so the store travels with the workspace and never reaches a tracked file. A name is confined to
/// <c>[a-z0-9-]</c>, which is what stops a note from naming a path outside the directory. The body is written verbatim
/// and is never evaluated: this is text the model chose to save, not an operating rule.
/// </remarks>
public static class ProjectMemoryStore
{
    /// <summary>Gets the largest note the store accepts, in bytes.</summary>
    public const int MaxNoteBytes = 16 * 1024;

    /// <summary>Gets the most notes the store holds.</summary>
    public const int MaxNotes = 64;

    /// <summary>Gets the note file extension.</summary>
    public const string Extension = ".md";

    /// <summary>Gets the absolute directory that holds the notes.</summary>
    /// <param name="workspace">The workspace whose store is wanted.</param>
    /// <returns>The absolute store path.</returns>
    public static string DirectoryPath(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return Path.Combine(workspace.Root, ".sharpcoreagent", "memory");
    }

    /// <summary>Normalises a model-supplied name into a slug, or explains why it cannot be one.</summary>
    /// <param name="name">The name the model wrote.</param>
    /// <param name="slug">The slug when the name is valid.</param>
    /// <param name="error">Why the name is invalid, otherwise null.</param>
    /// <returns><see langword="true"/> when the name is a usable slug.</returns>
    public static bool TrySlug(string? name, out string slug, out string? error)
    {
        slug = string.Empty;
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "a note name is required.";
            return false;
        }

        var normalized = name.Trim().ToLowerInvariant().Replace(' ', '-');
        if (normalized.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
        {
            error = "a note name may use only letters, digits and dashes (spaces become dashes).";
            return false;
        }

        if (normalized.Length > 64)
        {
            error = "a note name must be at most 64 characters.";
            return false;
        }

        slug = normalized;
        return true;
    }

    /// <summary>Lists the stored notes, ordered by name.</summary>
    /// <param name="workspace">The workspace whose store is read.</param>
    /// <returns>The notes; an absent store is an empty list.</returns>
    public static IReadOnlyList<ProjectMemoryNote> List(Workspace workspace)
    {
        var directory = DirectoryPath(workspace);
        if (!System.IO.Directory.Exists(directory))
        {
            return [];
        }

        var notes = new List<ProjectMemoryNote>();
        foreach (var file in System.IO.Directory.EnumerateFiles(directory, string.Concat("*", Extension)))
        {
            var info = new FileInfo(file);
            notes.Add(new ProjectMemoryNote(
                Path.GetFileNameWithoutExtension(file),
                info.LastWriteTimeUtc,
                info.Length));
        }

        notes.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        return notes;
    }

    /// <summary>Reads one note.</summary>
    /// <param name="workspace">The workspace whose store is read.</param>
    /// <param name="slug">The validated note name.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The note body, or null when no such note exists.</returns>
    public static async Task<string?> ReadAsync(Workspace workspace, string slug, CancellationToken cancellationToken)
    {
        var path = Path.Combine(DirectoryPath(workspace), string.Concat(slug, Extension));
        return File.Exists(path)
            ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <summary>Creates or replaces one note within the store's ceilings.</summary>
    /// <param name="workspace">The workspace whose store is written.</param>
    /// <param name="slug">The validated note name.</param>
    /// <param name="body">The note text.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Whether the write happened, whether it created the note, and the byte count or the refusal.</returns>
    public static async Task<ProjectMemoryWrite> WriteAsync(
        Workspace workspace,
        string slug,
        string body,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetByteCount(body);
        if (bytes > MaxNoteBytes)
        {
            return new ProjectMemoryWrite(
                false,
                false,
                bytes,
                string.Create(CultureInfo.InvariantCulture, $"the note is {bytes} bytes; the ceiling is {MaxNoteBytes}."));
        }

        var directory = DirectoryPath(workspace);
        System.IO.Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, string.Concat(slug, Extension));
        var created = !File.Exists(path);

        if (created
            && System.IO.Directory.EnumerateFiles(directory, string.Concat("*", Extension)).Count() >= MaxNotes)
        {
            return new ProjectMemoryWrite(
                false,
                false,
                bytes,
                string.Create(CultureInfo.InvariantCulture, $"the store already holds {MaxNotes} notes; update or remove one before adding another."));
        }

        await File.WriteAllTextAsync(path, body, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return new ProjectMemoryWrite(true, created, bytes, null);
    }
}

/// <summary>One stored note as <see cref="ProjectMemoryStore.List"/> reports it.</summary>
/// <param name="Name">The note's slug.</param>
/// <param name="ModifiedUtc">When the note was last written.</param>
/// <param name="Bytes">The size of the note on disk.</param>
public sealed record ProjectMemoryNote(string Name, DateTimeOffset ModifiedUtc, long Bytes);

/// <summary>The outcome of one <see cref="ProjectMemoryStore.WriteAsync"/>.</summary>
/// <param name="Ok">Whether the note was written.</param>
/// <param name="Created">Whether the write created a new note rather than replacing one.</param>
/// <param name="Bytes">The size of the note, or of the attempted note when refused.</param>
/// <param name="Error">Why the write was refused, otherwise null.</param>
public sealed record ProjectMemoryWrite(bool Ok, bool Created, long Bytes, string? Error);

