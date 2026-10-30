namespace SharpCoreAgent.Domain.Code;

/// <summary>
/// How serious a compiler diagnostic is.
/// </summary>
/// <remarks>
/// Three values, not four. A language service also produces hidden entries that exist only to colour an editor
/// (an unused <c>open</c>, a redundant qualifier). Those are not compiler output, so an adapter drops them
/// instead of inventing a fourth severity that no tool would render.
/// </remarks>
public enum CodeSeverity
{
    /// <summary>The file does not compile.</summary>
    Error,

    /// <summary>The file compiles, and the compiler believes it is probably wrong.</summary>
    Warning,

    /// <summary>The file compiles; the compiler has a suggestion.</summary>
    Info,
}
