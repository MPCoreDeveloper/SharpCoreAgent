using System.Globalization;

namespace SharpCoreAgent.Domain.Index;

/// <summary>
/// One search request, already validated.
/// </summary>
/// <remarks>
/// <see cref="Create"/> is the only constructor a caller needs, and it is the one place the bounds live.
/// The tool and the CLI both go through it, which is what makes them reject the same input with the same
/// words instead of drifting apart as each grows its own argument reader.
/// <para>
/// Out-of-range block counts are clamped rather than rejected: a model that asks for twenty blocks gets
/// twelve, because the budget is this side's problem, not the caller's. A kind that is not in the closed
/// list is rejected, because silently answering a different question is worse than failing.
/// </para>
/// </remarks>
public sealed record CodeQuery
{
    /// <summary>The default number of blocks a search returns.</summary>
    public const int DefaultK = 8;

    /// <summary>The largest number of blocks a search returns.</summary>
    public const int MaxK = 12;

    /// <summary>The default graph expansion depth.</summary>
    public const int DefaultHops = 1;

    /// <summary>The deepest graph expansion a search performs.</summary>
    public const int MaxHops = 3;

    /// <summary>The kind that matches every declaration.</summary>
    public const string AnyKind = "any";

    // Declaration order matters twice: SupportedKinds is initialised from this array, and the rendered
    // error message lists the same order the JSON schema does.
    private static readonly string[] KindNames =
    [
        AnyKind,
        "class",
        "interface",
        "record",
        "struct",
        "enum",
        "method",
        "property",
        "field",
        "doc",
        "config",
    ];

    /// <summary>
    /// Gets the closed list of values <see cref="Kind"/> may take, in schema order.
    /// </summary>
    public static IReadOnlyList<string> SupportedKinds { get; } = Array.AsReadOnly(KindNames);

    /// <summary>The evidence kind that matches every file.</summary>
    public const string AnyEvidenceKind = "any";

    // Declaration order matters twice here too: SupportedEvidenceKinds is initialised from this array, and the
    // rendered error message lists the same order the JSON schema does.
    private static readonly string[] EvidenceKindNames =
    [
        AnyEvidenceKind,
        "code",
        "document",
        "configuration",
        "other",
    ];

    /// <summary>
    /// Gets the closed list of values <see cref="EvidenceKind"/> may take, in schema order.
    /// </summary>
    public static IReadOnlyList<string> SupportedEvidenceKinds { get; } = Array.AsReadOnly(EvidenceKindNames);

    /// <summary>
    /// Gets the words or identifiers to look for.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Gets the workspace-relative file or directory to limit the search to, with <c>/</c> separators.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Gets the declaration kind to restrict the search to, or <see cref="AnyKind"/>.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>
    /// Gets the indexed language to restrict the search to, lowercased (<c>csharp</c>, <c>markdown</c>, <c>json</c>),
    /// or <see langword="null"/> for every language.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Gets a workspace-relative path prefix whose files the search must skip, or <see langword="null"/>.
    /// </summary>
    public string? ExcludePathPrefix { get; init; }

    /// <summary>
    /// Gets the coarse evidence kind to restrict the search to, or <see cref="AnyEvidenceKind"/>.
    /// </summary>
    public string EvidenceKind { get; init; } = AnyEvidenceKind;

    /// <summary>
    /// Gets how many blocks to return, between 1 and <see cref="MaxK"/>.
    /// </summary>
    public int K { get; init; } = DefaultK;

    /// <summary>
    /// Gets how far to expand through the symbol graph before ranking, between 0 and <see cref="MaxHops"/>.
    /// </summary>
    public int Hops { get; init; } = DefaultHops;

    /// <summary>
    /// Validates and normalises one request.
    /// </summary>
    /// <param name="text">The words or identifiers to look for; trimmed, and it may not be blank.</param>
    /// <param name="path">A workspace-relative path, or <see langword="null"/> for the whole workspace.</param>
    /// <param name="kind">One of <see cref="SupportedKinds"/>, case-insensitive, or <see langword="null"/>.</param>
    /// <param name="k">The number of blocks wanted; clamped to 1–12.</param>
    /// <param name="hops">The graph depth wanted; clamped to 0–3.</param>
    /// <param name="language">An indexed language to restrict to, or <see langword="null"/> for every language.</param>
    /// <param name="excludePathPrefix">A workspace-relative prefix to skip, or <see langword="null"/>.</param>
    /// <param name="evidenceKind">One of <see cref="SupportedEvidenceKinds"/>, case-insensitive, or <see langword="null"/>.</param>
    /// <returns>A query every retriever can execute without checking anything again.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="text"/> is blank, <paramref name="kind"/> or <paramref name="evidenceKind"/> is not in its
    /// closed list, or <paramref name="path"/> or <paramref name="excludePathPrefix"/> is rooted or climbs out of the
    /// workspace.
    /// </exception>
    public static CodeQuery Create(
        string text,
        string? path = null,
        string? kind = null,
        int k = DefaultK,
        int hops = DefaultHops,
        string? language = null,
        string? excludePathPrefix = null,
        string? evidenceKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        return new CodeQuery
        {
            Text = text.Trim(),
            Path = NormalizePath(path),
            Kind = CanonicalKind(kind),
            K = Math.Clamp(k, 1, MaxK),
            Hops = Math.Clamp(hops, 0, MaxHops),
            Language = NormalizeLanguage(language),
            ExcludePathPrefix = NormalizePath(excludePathPrefix),
            EvidenceKind = CanonicalEvidenceKind(evidenceKind),
        };
    }

    /// <summary>
    /// Renders a caller-supplied path the way the index stores it: forward slashes, no leading
    /// <c>./</c>, no trailing slash, and no way out of the workspace.
    /// </summary>
    /// <param name="path">A workspace-relative path, or <see langword="null"/>.</param>
    /// <returns>The normalised path, or <see langword="null"/> when the input names the whole workspace.</returns>
    /// <exception cref="ArgumentException">
    /// The path is rooted, contains a NUL character, or has a <c>..</c> segment.
    /// </exception>
    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = path.Trim();

        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("a path may not contain a NUL character.", nameof(path));
        }

        // A rooted path is not a filter inside this workspace; it names another tree.
        if (System.IO.Path.IsPathRooted(value))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"path '{path}' must be relative to the workspace root."),
                nameof(path));
        }

        value = value.Replace('\\', '/');

        while (value.StartsWith("./", StringComparison.Ordinal))
        {
            value = value[2..];
        }

        value = value.TrimEnd('/');

        if (value.Length == 0 || string.Equals(value, ".", StringComparison.Ordinal))
        {
            return null;
        }

        if (value.Split('/').Any(segment => string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"path '{path}' must stay inside the workspace root."),
                nameof(path));
        }

        return value;
    }

    private static string CanonicalKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return AnyKind;
        }

        var trimmed = kind.Trim();

        // Case-insensitive rather than lowercasing the input: the closed list is already lowercase, so
        // returning the matching entry gives the canonical spelling without allocating per call.
        if (KindNames.FirstOrDefault(candidate => string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
            is { } canonical)
        {
            return canonical;
        }

        throw new ArgumentException(
            string.Create(CultureInfo.InvariantCulture, $"kind must be one of {string.Join(", ", KindNames)}, but was '{kind}'."),
            nameof(kind));
    }

    /// <summary>
    /// Trims and lowercases a language filter so it matches the label the scanner stores.
    /// </summary>
    /// <param name="language">The argument as the model wrote it, or <see langword="null"/>.</param>
    /// <returns>The lowercased label, or <see langword="null"/> for "every language".</returns>
    /// <remarks>
    /// Unlike <see cref="Kind"/> there is no closed list: the set of languages the scanner can report is open, so a
    /// caller may name one this build does not index and simply match nothing. Lowercasing keeps the query canonical
    /// and makes the comparison against the stored label a plain string equality.
    /// </remarks>
    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language) ? null : language.Trim().ToLowerInvariant();

    private static string CanonicalEvidenceKind(string? evidenceKind)
    {
        if (string.IsNullOrWhiteSpace(evidenceKind))
        {
            return AnyEvidenceKind;
        }

        var trimmed = evidenceKind.Trim();

        if (EvidenceKindNames.FirstOrDefault(candidate => string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
            is { } canonical)
        {
            return canonical;
        }

        throw new ArgumentException(
            string.Create(CultureInfo.InvariantCulture, $"evidence_kind must be one of {string.Join(", ", EvidenceKindNames)}, but was '{evidenceKind}'."),
            nameof(evidenceKind));
    }
}
