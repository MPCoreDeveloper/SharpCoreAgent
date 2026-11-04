using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SharpCoreAgent.Domain.Code;

namespace SharpCoreAgent.Languages.CSharp;

/// <summary>One C# project as the in-memory Roslyn solution needs it.</summary>
internal sealed record CsProjectModel
{
    public required string ProjectFile { get; init; }
    public required string TargetFramework { get; init; }
    public required string AssemblyName { get; init; }
    public required IReadOnlyList<string> Sources { get; init; }
    public required IReadOnlyList<string> ReferencePaths { get; init; }
    public required IReadOnlyList<string> ProjectReferences { get; init; }
    public required IReadOnlyList<InMemorySource> InMemorySources { get; init; }
    public required CSharpParseOptions ParseOptions { get; init; }
    public required CSharpCompilationOptions CompilationOptions { get; init; }
    public required string InputStamp { get; init; }
}

/// <summary>A source Roslyn is given that is not part of the project's files, such as generated global usings.</summary>
internal sealed record InMemorySource(string Path, string Text);

/// <summary>The outcome of describing one project: a model, or the reason there is none.</summary>
internal readonly record struct CsProjectLoad(CsProjectModel? Project, CodeFailure? Failure)
{
    public static CsProjectLoad Of(CsProjectModel project) => new(project, null);
    public static CsProjectLoad Failed(CodeFailure failure) => new(null, failure);
}
