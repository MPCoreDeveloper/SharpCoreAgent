namespace SharpCoreAgent.Indexing.Ranking;

/// <summary>
/// The one place a file's stored language is turned into the coarse evidence kind a search filter names.
/// </summary>
/// <remarks>
/// The index stores a language label per file (the scanner's extension map) and nothing about what that language is
/// <em>for</em>. A caller that asks for "code" or "documents" is asking a question the raw label cannot answer on its
/// own, so the sets below are the bridge — kept here, beside the ranker that applies them, so the <c>doc</c> and
/// <c>config</c> query kinds and the <c>evidence_kind</c> filter classify in exactly the same way.
/// <para>
/// A language the maps do not know is reported as <c>other</c> rather than guessed into a bucket: the scanner's
/// catch-all label is <c>text</c>, and calling a stray <c>.fs</c> file a "document" would be an invented fact.
/// </para>
/// </remarks>
internal static class EvidenceKinds
{
    /// <summary>The evidence-kind spelling for source code.</summary>
    internal const string Code = "code";

    /// <summary>The evidence-kind spelling for prose documentation.</summary>
    internal const string Document = "document";

    /// <summary>The evidence-kind spelling for configuration.</summary>
    internal const string Configuration = "configuration";

    /// <summary>The evidence-kind spelling for anything unrecognised.</summary>
    internal const string Other = "other";

    /// <summary>The languages classified as source code.</summary>
    internal static readonly HashSet<string> CodeLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "csharp", "typescript", "javascript", "python", "go", "rust", "java", "kotlin",
        "c", "cpp", "ruby", "php", "swift", "sql", "shell", "powershell", "html", "css", "razor",
    };

    /// <summary>The languages classified as prose documentation.</summary>
    internal static readonly HashSet<string> DocumentLanguages = new(StringComparer.OrdinalIgnoreCase) { "markdown" };

    /// <summary>The languages classified as configuration.</summary>
    internal static readonly HashSet<string> ConfigurationLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "yaml", "toml", "xml",
    };

    /// <summary>
    /// Classifies one stored language label.
    /// </summary>
    /// <param name="language">The file's indexed language, or <see langword="null"/>.</param>
    /// <returns><c>code</c>, <c>document</c>, <c>configuration</c>, or <c>other</c> for anything unrecognised.</returns>
    internal static string Classify(string? language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return Other;
        }

        if (CodeLanguages.Contains(language))
        {
            return Code;
        }

        if (DocumentLanguages.Contains(language))
        {
            return Document;
        }

        return ConfigurationLanguages.Contains(language) ? Configuration : Other;
    }

    /// <summary>
    /// Reports whether a language is prose documentation.
    /// </summary>
    /// <param name="language">The file's indexed language, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> for a documentation language.</returns>
    internal static bool IsDocument(string? language) =>
        !string.IsNullOrEmpty(language) && DocumentLanguages.Contains(language);

    /// <summary>
    /// Reports whether a language is configuration.
    /// </summary>
    /// <param name="language">The file's indexed language, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> for a configuration language.</returns>
    internal static bool IsConfiguration(string? language) =>
        !string.IsNullOrEmpty(language) && ConfigurationLanguages.Contains(language);
}
