using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using SharpCoreAgent.Domain.Code;

namespace SharpCoreAgent.Languages.CSharp;

/// <summary>Materialises a project graph as an in-memory Roslyn solution.</summary>
internal static class CsSolutionBuilder
{
    public static CsSnapshotLoad Build(
        IReadOnlyDictionary<string, CsProjectModel> models,
        string stamp,
        string targetProject,
        CancellationToken cancellationToken)
    {
        var workspace = new AdhocWorkspace();
        try
        {
            var ids = models.Values.ToDictionary(
                model => model.ProjectFile,
                _ => ProjectId.CreateNewId(),
                StringComparer.OrdinalIgnoreCase);
            var ordered = new List<CsProjectModel>();
            Order(models[targetProject], models, ordered, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var solution = workspace.CurrentSolution;

            foreach (var model in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var references = new List<MetadataReference>(model.ReferencePaths.Count);
                foreach (var path in model.ReferencePaths)
                {
                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(path));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
                    {
                        throw new InvalidOperationException($"Reference '{Path.GetFileName(path)}' could not be read: {ex.Message}", ex);
                    }
                }

                var projectReferences = model.ProjectReferences.Select(path => new ProjectReference(ids[path])).ToArray();
                var info = ProjectInfo.Create(
                    ids[model.ProjectFile],
                    VersionStamp.Create(),
                    name: Path.GetFileNameWithoutExtension(model.ProjectFile),
                    assemblyName: model.AssemblyName,
                    language: LanguageNames.CSharp,
                    compilationOptions: model.CompilationOptions,
                    parseOptions: model.ParseOptions,
                    metadataReferences: references,
                    projectReferences: projectReferences);
                solution = solution.AddProject(info);
                solution = AddDocuments(solution, model, ids[model.ProjectFile], cancellationToken);
            }

            if (!workspace.TryApplyChanges(solution))
            {
                throw new InvalidOperationException("The in-memory C# solution could not be applied.");
            }

            return CsSnapshotLoad.Of(new CsSolutionSnapshot
            {
                Stamp = stamp,
                Workspace = workspace,
                ProjectIds = ids,
                Models = models,
                TargetProjectFile = targetProject,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            workspace.Dispose();
            return CsSnapshotLoad.Failed(CsFailures.NotAssembled(ex.Message));
        }
    }

    private static Solution AddDocuments(
        Solution solution,
        CsProjectModel model,
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        foreach (var path in model.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId, Path.GetFileName(path)),
                Path.GetFileName(path),
                SourceText.From(File.ReadAllText(path), Encoding.UTF8),
                filePath: path);
        }

        foreach (var extra in model.InMemorySources)
        {
            solution = solution.AddDocument(
                DocumentId.CreateNewId(projectId, Path.GetFileName(extra.Path)),
                Path.GetFileName(extra.Path),
                SourceText.From(extra.Text, Encoding.UTF8),
                filePath: extra.Path);
        }

        return solution;
    }

    private static void Order(
        CsProjectModel model,
        IReadOnlyDictionary<string, CsProjectModel> models,
        List<CsProjectModel> ordered,
        HashSet<string> visited)
    {
        if (!visited.Add(model.ProjectFile))
        {
            return;
        }

        foreach (var reference in model.ProjectReferences)
        {
            Order(models[reference], models, ordered, visited);
        }

        ordered.Add(model);
    }
}
