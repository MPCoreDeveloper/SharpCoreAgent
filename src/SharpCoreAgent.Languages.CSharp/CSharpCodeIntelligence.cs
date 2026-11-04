using Microsoft.CodeAnalysis;
using SharpCoreAgent.Domain.Code;
using AgentWorkspace = SharpCoreAgent.Domain.Workspace;

namespace SharpCoreAgent.Languages.CSharp;

/// <summary>
/// C# code intelligence: diagnostics, compiler-bound uses and signature help from an in-memory Roslyn solution.
/// </summary>
/// <remarks>
/// Project XML and restored assets are read directly. No <c>dotnet</c>, MSBuild build host, restore or language server is
/// launched, which is the same direction of travel as the F# adapter. A snapshot is rebuilt when any project, source,
/// directory build file, asset file or reference path stamp changes; otherwise repeated calls reuse one solution.
/// </remarks>
public sealed class CSharpCodeIntelligence : ICodeDiagnostics, ISymbolUses, ISignatureHelp, ILanguageAdapter
{
    private readonly AgentWorkspace _workspace;
    private readonly CsProjectSource _source;
    private readonly CsSolutionCache _cache;

    /// <summary>Creates the adapter for one workspace.</summary>
    /// <param name="workspace">The workspace whose files it answers about.</param>
    public CSharpCodeIntelligence(AgentWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _workspace = workspace;
        _source = new CsProjectSource(workspace);
        _cache = new CsSolutionCache(_source);
    }

    /// <inheritdoc />
    public bool Supports(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async ValueTask<CodeDiagnosticsResult> GetDiagnosticsAsync(string path, CancellationToken cancellationToken)
    {
        var (snapshot, fullPath, failure) = Resolve(path, cancellationToken);
        if (failure is not null)
        {
            return CodeDiagnosticsResult.Failed(failure);
        }

        try
        {
            var project = snapshot!.Solution.GetProject(snapshot.TargetProjectId)
                ?? throw new InvalidOperationException("The target project disappeared from the in-memory solution.");
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Roslyn produced no compilation for this project.");
            var diagnostics = new List<CodeDiagnostic>();
            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
            {
                if (!diagnostic.Location.IsInSource
                    || diagnostic.Location.SourceTree?.FilePath is not { } diagnosticPath
                    || !SamePath(diagnosticPath, fullPath!))
                {
                    continue;
                }

                if (Severity(diagnostic.Severity) is not { } severity)
                {
                    continue;
                }

                var position = diagnostic.Location.GetLineSpan();
                diagnostics.Add(new CodeDiagnostic
                {
                    Severity = severity,
                    Id = diagnostic.Id,
                    Path = Relative(diagnosticPath),
                    Line = position.StartLinePosition.Line + 1,
                    Column = position.StartLinePosition.Character,
                    Message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture).ReplaceLineEndings(" ").Trim(),
                });
            }

            diagnostics.Sort(static (left, right) =>
                left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column));
            return CodeDiagnosticsResult.Ok(diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CodeDiagnosticsResult.Failed(CsFailures.AnalysisFailed("Roslyn could not check this C# file: " + ex.Message));
        }
    }

    /// <inheritdoc />
    public async ValueTask<SymbolUsesResult> FindUsesAsync(
        string path,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var (snapshot, fullPath, failure) = Resolve(path, cancellationToken);
        return failure is not null
            ? SymbolUsesResult.Failed(failure)
            : await CsSemanticQueries.FindUsesAsync(
                snapshot!, _workspace, fullPath!, line, column, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<SignatureHelpResult> GetSignatureAsync(
        string path,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var (snapshot, fullPath, failure) = Resolve(path, cancellationToken);
        return failure is not null
            ? SignatureHelpResult.Failed(failure)
            : await CsSignatureQueries.GetSignatureAsync(
                snapshot!, fullPath!, line, column, cancellationToken).ConfigureAwait(false);
    }

    private (CsSolutionSnapshot? Snapshot, string? FullPath, CodeFailure? Failure) Resolve(
        string path,
        CancellationToken cancellationToken)
    {
        if (!Supports(path))
        {
            return (null, null, CsFailures.NotCSharp(path));
        }

        if (!_workspace.TryResolveRead(path, out var fullPath, out _, out _) || !File.Exists(fullPath))
        {
            return (null, null, CsFailures.FileMissing(path));
        }

        var projects = CsProjectProbe.Find(_workspace);
        if (projects.Count == 0)
        {
            return (null, null, CsFailures.NoProject());
        }

        foreach (var candidate in CsProjectProbe.Nearest(fullPath, projects))
        {
            var load = _source.Load(candidate, cancellationToken);
            if (load.Project is not { } project)
            {
                return (null, null, load.Failure);
            }

            if (!project.Sources.Contains(fullPath, PathComparer))
            {
                continue;
            }

            var snapshot = _cache.Get(project, cancellationToken);
            return snapshot.Snapshot is null
                ? (null, null, snapshot.Failure)
                : (snapshot.Snapshot, fullPath, null);
        }

        return (null, null, CsFailures.NotAssembled(
            $"'{path}' is not a source of any C# project under this workspace, so the compiler has nothing to check."));
    }

    private string Relative(string fullPath) => _workspace.ToRelative(fullPath).Replace('\\', '/');

    private static bool SamePath(string left, string right) => string.Equals(left, right, PathComparison);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static CodeSeverity? Severity(DiagnosticSeverity severity) =>
        severity switch
        {
            DiagnosticSeverity.Error => CodeSeverity.Error,
            DiagnosticSeverity.Warning => CodeSeverity.Warning,
            DiagnosticSeverity.Info => CodeSeverity.Info,
            _ => null,
        };
}
