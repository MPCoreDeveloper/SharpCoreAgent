namespace SharpCoreAgent.Indexing.Extraction;

/// <summary>
/// One file the scanner walked past: either something to extract or something to record as skipped.
/// </summary>
/// <remarks>
/// A skipped file is a result, not an absence. The index keeps a row for it with a
/// <see cref="SkipReason"/>, so "why is this repository's <c>.png</c> table not searchable?" has an answer in
/// <c>index status</c> instead of a shrug.
/// </remarks>
internal sealed record ScannedFile
{
    /// <summary>Gets the workspace-relative path with <c>/</c> separators.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the absolute path, which is what a reader opens.</summary>
    public required string FullPath { get; init; }

    /// <summary>Gets the language the extension implies, or <c>text</c> when it implies nothing.</summary>
    public required string Language { get; init; }

    /// <summary>Gets the size on disk, in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Gets when the file last changed, in UTC.</summary>
    public required DateTimeOffset ModifiedUtc { get; init; }

    /// <summary>Gets why the file was not extracted, or <see langword="null"/> when it was.</summary>
    public string? SkipReason { get; init; }

    /// <summary>Gets a value indicating whether the file has content to extract.</summary>
    public bool IsIndexable => SkipReason is null;
}
