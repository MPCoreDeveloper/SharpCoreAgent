using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using SharpCoreAgent.Domain.Code;

namespace SharpCoreAgent.Languages.CSharp;

/// <summary>The target document, its semantic model and its text, loaded once per query.</summary>
internal sealed record CsDocumentContext(
    Document Document,
    SemanticModel Model,
    SyntaxNode Root,
    SourceText Text)
{
    public static async Task<(CsDocumentContext? Context, CodeFailure? Failure)> LoadAsync(
        CsSolutionSnapshot snapshot,
        string fullPath,
        CancellationToken cancellationToken)
    {
        var project = snapshot.Solution.GetProject(snapshot.TargetProjectId);
        var document = project?.Documents.FirstOrDefault(document => string.Equals(
            document.FilePath,
            fullPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        if (document is null)
        {
            return (null, CsFailures.AnalysisFailed("The requested C# file is not a document in the in-memory project."));
        }

        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        return root is null || model is null
            ? (null, CsFailures.AnalysisFailed("Roslyn produced no syntax tree or semantic model for this file."))
            : (new CsDocumentContext(document, model, root, text), null);
    }

    public bool TryPosition(int line, int column, out int position)
    {
        position = -1;
        if (line < 1 || line > Text.Lines.Count)
        {
            return false;
        }

        var sourceLine = Text.Lines[line - 1];
        if (column < 0 || column > sourceLine.Span.Length)
        {
            return false;
        }

        position = sourceLine.Start + column;
        return true;
    }
}
