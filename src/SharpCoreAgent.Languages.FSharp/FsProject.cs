namespace SharpCoreAgent.Languages.FSharp;

/// <summary>
/// One F# project as the checker needs it: the file the compiler calls the project, the sources in the order the
/// project declares them, and the arguments that describe everything else.
/// </summary>
/// <remarks>
/// A record rather than a service, because everything downstream of it is pure. <see cref="Arguments"/> is the
/// entire input to the checker, so a test can build one from a fixture and never touch MSBuild, NuGet or the
/// network — which is what makes an F# code-intelligence test a unit test instead of an integration test.
/// <para>
/// Sources are absolute and ordered. F# resolves names by file order, so an unordered set is not a project: the
/// order here is the order the <c>.fsproj</c> declares, which is the only order the compiler accepts.
/// </para>
/// </remarks>
internal sealed record FsProject
{
    /// <summary>
    /// Gets the absolute path of the project file, which the checker attributes diagnostics to.
    /// </summary>
    public required string ProjectFile { get; init; }

    /// <summary>
    /// Gets the absolute source paths, in compilation order.
    /// </summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>
    /// Gets the argument list: options first, then one <c>-r:</c> per reference, then the sources.
    /// </summary>
    public required IReadOnlyList<string> Arguments { get; init; }
}
