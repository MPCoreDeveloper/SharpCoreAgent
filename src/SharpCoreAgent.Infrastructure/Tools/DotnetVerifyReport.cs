namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// What one <c>dotnet build</c> or <c>dotnet test</c> run said, as <c>dotnet_verify</c> reports it.
/// </summary>
/// <remarks>
/// A record of what the log contained, not a second opinion about it: the exit code decides whether the run
/// passed, and the parser's job is to name the summary, the diagnostics and the counts that a model and a
/// developer both need. Nothing here is inferred, so a report that disagrees with the log is a parsing bug
/// rather than a policy decision — which is what makes the parser testable without a compiler.
/// </remarks>
public sealed record DotnetVerifyReport
{
    /// <summary>Gets a value indicating whether the run passed: exit code 0 and no failure marker or diagnostic.</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Gets the process exit code.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Gets the verdict line the log printed, such as <c>Build succeeded.</c>, or <c>exit code 1</c> when it printed none.</summary>
    public required string Summary { get; init; }

    /// <summary>Gets the number of warnings the run reported.</summary>
    public int WarningCount { get; init; }

    /// <summary>Gets the distinct diagnostic lines, in the order they appeared, capped by the parser.</summary>
    /// <remarks>
    /// The cap exists because a header is a summary: a build that fails four thousand times is one problem, and
    /// the log below the header still holds every line. A caller that needs them all reads the raw output.
    /// </remarks>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>Gets how many tests passed, when the log carried a test summary.</summary>
    public int? TestsPassed { get; init; }

    /// <summary>Gets how many tests failed, when the log carried a test summary.</summary>
    public int? TestsFailed { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Summary} (exit code {ExitCode}, {Errors.Count} error(s), {WarningCount} warning(s))";
}
