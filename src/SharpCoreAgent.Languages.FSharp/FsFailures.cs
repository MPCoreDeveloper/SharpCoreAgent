using SharpCoreAgent.Domain.Code;

namespace SharpCoreAgent.Languages.FSharp;

/// <summary>
/// Every way F# code intelligence can decline to answer, in one place.
/// </summary>
/// <remarks>
/// The codes are the contract and the messages are documentation: a test asserts <c>FSH001</c>, a model reads the
/// sentence. Keeping them here rather than inline at six call sites is what stops two of them from drifting into
/// the same code with different meanings.
/// <para>
/// Every hint names a tool this repository actually registers. A hint is an instruction the model will follow, so
/// one that suggests a command the agent does not have turns a recoverable failure into a dead end.
/// </para>
/// </remarks>
internal static class FsFailures
{
    /// <summary>The workspace holds no F# project.</summary>
    /// <returns>A failure pointing at the tool that lists what the workspace does hold.</returns>
    public static CodeFailure NoProject() => new()
    {
        Code = "FSH001",
        Message = "No F# project (.fsproj) was found in the workspace, so there is nothing to type-check against.",
        Hint = "Call list_roots to confirm the workspace root, or read_file the project directly.",
    };

    /// <summary>The project exists but cannot be described well enough to check it.</summary>
    /// <param name="detail">What is missing, in one sentence.</param>
    /// <returns>A failure pointing at the build, which is what produces the missing description.</returns>
    public static CodeFailure NotAssembled(string detail) => new()
    {
        Code = "FSH002",
        Message = detail,
        Hint = "Run dotnet_verify kind=build so the project is restored and its references are on disk.",
    };

    /// <summary>Nothing a compiler can name sits at the requested position.</summary>
    /// <param name="line">The 1-based line that was asked about.</param>
    /// <param name="column">The 0-based column that was asked about.</param>
    /// <returns>A failure pointing at the tools that answer without a compiler.</returns>
    public static CodeFailure NothingAtPosition(int line, int column) => new()
    {
        Code = "FSH003",
        Message = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Nothing nameable is at {line}:{column}."),
        Hint = "Use find_references for a name-based search, or read_file to check the position.",
    };

    /// <summary>The path is not an F# source, so this language has no opinion about it.</summary>
    /// <param name="path">The path that was asked about.</param>
    /// <returns>A failure pointing at the language-neutral tools.</returns>
    public static CodeFailure NotFSharp(string path) => new()
    {
        Code = "FSH004",
        Message = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"'{path}' is not an F# source file, so F# code intelligence cannot answer for it."),
        Hint = "Use find_references or search_code for other file types, and dotnet_verify for their compiler errors.",
    };

    /// <summary>The checker failed or ran out of its budget.</summary>
    /// <param name="detail">What the checker reported, in one sentence.</param>
    /// <returns>A failure pointing at the build, which reports the same problem more slowly.</returns>
    public static CodeFailure CheckerFailed(string detail) => new()
    {
        Code = "FSH005",
        Message = detail,
        Hint = "Run dotnet_verify to get the compiler's own answer, or retry once the project builds.",
    };

    /// <summary>The path does not exist inside the workspace.</summary>
    /// <param name="path">The path that was asked about.</param>
    /// <returns>A failure pointing at the tool that reads files and the one that lists roots.</returns>
    public static CodeFailure FileMissing(string path) => new()
    {
        Code = "FSH006",
        Message = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"'{path}' does not exist inside the workspace."),
        Hint = "Use list_roots or search_code to find the file you meant.",
    };
}
