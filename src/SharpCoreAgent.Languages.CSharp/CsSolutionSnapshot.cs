using Microsoft.CodeAnalysis;
using SharpCoreAgent.Domain.Code;

namespace SharpCoreAgent.Languages.CSharp;

/// <summary>One cached in-memory solution and the project models that produced it.</summary>
internal sealed class CsSolutionSnapshot
{
    public required string Stamp { get; init; }
    public required AdhocWorkspace Workspace { get; init; }
    public required IReadOnlyDictionary<string, ProjectId> ProjectIds { get; init; }
    public required IReadOnlyDictionary<string, CsProjectModel> Models { get; init; }
    public required string TargetProjectFile { get; init; }

    public Solution Solution => Workspace.CurrentSolution;

    public ProjectId TargetProjectId => ProjectIds[TargetProjectFile];
}

internal readonly record struct CsSnapshotLoad(CsSolutionSnapshot? Snapshot, CodeFailure? Failure)
{
    public static CsSnapshotLoad Of(CsSolutionSnapshot snapshot) => new(snapshot, null);
    public static CsSnapshotLoad Failed(CodeFailure failure) => new(null, failure);
}
