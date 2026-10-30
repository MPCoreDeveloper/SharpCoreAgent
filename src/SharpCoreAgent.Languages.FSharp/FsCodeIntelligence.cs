using System.Globalization;
using FSharp.Compiler.CodeAnalysis;
using FSharp.Compiler.Diagnostics;
using FSharp.Compiler.EditorServices;
using FSharp.Compiler.Symbols;
using FSharp.Compiler.Text;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Control;
using Microsoft.FSharp.Core;
using SharpCoreAgent.Domain;
using SharpCoreAgent.Domain.Code;

namespace SharpCoreAgent.Languages.FSharp;

/// <summary>
/// F# code intelligence: diagnostics, compiler-bound symbol uses and signature help, answered in-process.
/// </summary>
/// <remarks>
/// The checker is linked in, not launched: no <c>dotnet</c>, no MSBuild, no restore, no network. That is the whole
/// claim this type has to keep, and it is why it can be offered to a model as a tool that is safe to call often.
/// <para>
/// The first request for a project pays for loading its references (seconds, mostly reading metadata); every later
/// request on the same project re-checks only what changed (milliseconds). The project's argument list is cached
/// until the <c>.fsproj</c> itself changes, and the checker's own parse cache does the rest.
/// </para>
/// <para>
/// Nothing here throws for a workspace problem: a missing project, an unrestored one, a position with no symbol and
/// an expired budget are all <see cref="CodeFailure"/> values. Instances are safe to share between turns; the only
/// mutable state is the argument cache, guarded by one lock.
/// </para>
/// </remarks>
public sealed class FsCodeIntelligence : ICodeDiagnostics, ISymbolUses, ISignatureHelp, ILanguageAdapter
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private readonly FSharpChecker _checker;
    private readonly Dictionary<string, (DateTime Stamp, FSharpProjectOptions Options)> _options =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly FSharpProjectProbe _probe;
    private readonly MsBuildFsProjectSource _source;
    private readonly Workspace _workspace;

    /// <summary>
    /// Creates the adapter for one workspace.
    /// </summary>
    /// <param name="workspace">The workspace whose files it answers about.</param>
    public FsCodeIntelligence(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        _workspace = workspace;
        _probe = new FSharpProjectProbe(workspace);
        _source = new MsBuildFsProjectSource(workspace);

        // Every F# optional argument is spelled out because C# cannot omit one, and only two matter:
        // keeping assembly contents is what makes a project-wide symbol-use query possible without re-checking
        // every file, and the rest keep their documented defaults.
        _checker = FSharpChecker.Create(
            projectCacheSize: null,
            keepAssemblyContents: FSharpOption<bool>.Some(true),
            keepAllBackgroundResolutions: null,
            legacyReferenceResolver: null,
            tryGetMetadataSnapshot: null,
            suggestNamesForErrors: null,
            keepAllBackgroundSymbolUses: null,
            enableBackgroundItemKeyStoreAndSemanticClassification: null,
            enablePartialTypeChecking: null,
            parallelReferenceResolution: null,
            captureIdentifiersWhenParsing: null,
            documentSource: null,
            useTransparentCompiler: null,
            transparentCompilerCacheSizes: null);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Extension only: <c>.fs</c> and <c>.fsi</c>. A <c>.fsx</c> script is deliberately not claimed, because a
    /// script's references come from <c>#r</c> directives the checker resolves by probing the machine, and a tool
    /// that answers by probing is not the tool this project ships.
    /// </remarks>
    public bool Supports(string path) =>
        path.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".fsi", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async ValueTask<CodeDiagnosticsResult> GetDiagnosticsAsync(string path, CancellationToken cancellationToken)
    {
        var (options, fullPath, failure) = Resolve(path, cancellationToken);
        if (failure is not null)
        {
            return CodeDiagnosticsResult.Failed(failure);
        }

        var (results, checkFailure) = await CheckProjectAsync(options!, cancellationToken).ConfigureAwait(false);
        if (results is null)
        {
            return CodeDiagnosticsResult.Failed(checkFailure!);
        }

        var diagnostics = new List<CodeDiagnostic>();
        foreach (var diagnostic in results.Diagnostics)
        {
            if (!SamePath(diagnostic.FileName, fullPath!))
            {
                continue;
            }

            var severity = MapSeverity(diagnostic.Severity);
            if (severity is null)
            {
                continue;
            }

            diagnostics.Add(new CodeDiagnostic
            {
                Severity = severity.Value,
                Id = diagnostic.ErrorNumberText,
                Path = _workspace.ToRelative(diagnostic.FileName),
                Line = diagnostic.StartLine,
                Column = diagnostic.StartColumn,
                Message = diagnostic.Message.ReplaceLineEndings(" ").Trim(),
            });
        }

        diagnostics.Sort(static (left, right) =>
            left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column));

        return CodeDiagnosticsResult.Ok(diagnostics);
    }

    /// <inheritdoc />
    public async ValueTask<SymbolUsesResult> FindUsesAsync(
        string path,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var (options, fullPath, failure) = Resolve(path, cancellationToken);
        if (failure is not null)
        {
            return SymbolUsesResult.Failed(failure);
        }

        var (projectResults, projectFailure) = await CheckProjectAsync(options!, cancellationToken).ConfigureAwait(false);
        if (projectResults is null)
        {
            return SymbolUsesResult.Failed(projectFailure!);
        }

        // The position is resolved against the project's own symbol uses rather than the file's, because that list
        // is where the answer comes from: one check instead of two, and no chance of resolving the position against
        // a different view of the file than the one that produced the uses.
        FSharpSymbol? subject = null;
        foreach (var candidate in projectResults.GetAllUsesOfAllSymbols(null))
        {
            if (!SamePath(candidate.FileName, fullPath!) || candidate.Range.StartLine != line)
            {
                continue;
            }

            if (column < candidate.Range.StartColumn || column > candidate.Range.EndColumn)
            {
                continue;
            }

            subject = candidate.Symbol;
            break;
        }

        if (subject is null)
        {
            return SymbolUsesResult.Failed(FsFailures.NothingAtPosition(line, column));
        }

        var uses = new List<SymbolUse>();
        foreach (var use in projectResults.GetUsesOfSymbol(subject, null))
        {
            uses.Add(new SymbolUse
            {
                Path = _workspace.ToRelative(use.FileName),
                Line = use.Range.StartLine,
                Column = use.Range.StartColumn,
                IsDefinition = use.IsFromDefinition,
                Display = use.Symbol.DisplayName,
            });
        }

        uses.Sort(static (left, right) =>
        {
            var byPath = string.CompareOrdinal(left.Path, right.Path);
            if (byPath != 0)
            {
                return byPath;
            }

            return left.Line != right.Line ? left.Line.CompareTo(right.Line) : left.Column.CompareTo(right.Column);
        });

        return SymbolUsesResult.Ok(uses);
    }

    /// <inheritdoc />
    public async ValueTask<SignatureHelpResult> GetSignatureAsync(
        string path,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        var (options, fullPath, failure) = Resolve(path, cancellationToken);
        if (failure is not null)
        {
            return SignatureHelpResult.Failed(failure);
        }

        var lineText = ReadLine(fullPath!, line);
        if (lineText is null)
        {
            return SignatureHelpResult.Failed(FsFailures.NothingAtPosition(line, column));
        }

        var (fileResults, fileFailure) = await CheckFileAsync(options!, fullPath!, cancellationToken).ConfigureAwait(false);
        if (fileResults is null)
        {
            return SignatureHelpResult.Failed(fileFailure!);
        }

        var activeParameter = ActiveParameterFinder.Find(lineText, column);

        // GetMethods is the checker's signature-help entry point: it resolves the call site without being given a
        // parser token, which the tooltip path requires and this adapter therefore cannot use.
        var group = fileResults.GetMethods(line, NameEnd(lineText, column), lineText, null);
        if (group is not null && group.Methods.Length > 0)
        {
            var signatures = new List<SignatureInfo>(group.Methods.Length);
            foreach (var item in group.Methods)
            {
                var display = ToolTipTextRenderer.Render(item.Description);
                var parameters = new List<string>(item.Parameters.Length);
                foreach (var parameter in item.Parameters)
                {
                    // Display is the whole label, name and type together; ParameterName alone loses the type.
                    var label = ToolTipTextRenderer.Render(parameter.Display);
                    parameters.Add(label.Length == 0 ? parameter.ParameterName : label);
                }

                signatures.Add(new SignatureInfo
                {
                    Display = display.Length == 0 ? group.MethodName : display,
                    Parameters = parameters,
                    ActiveParameter = activeParameter,
                });
            }

            return SignatureHelpResult.Ok(signatures, 0);
        }

        // A position on a declaration rather than a call has no call site, so the symbol's own type answers instead
        // of claiming there is no signature at all.
        var symbolUse = fileResults.GetSymbolUseAtLocation(line, NameEnd(lineText, column), lineText, FSharpList<string>.Empty);
        if (symbolUse?.Value.Symbol is not FSharpMemberOrFunctionOrValue function)
        {
            return SignatureHelpResult.Failed(FsFailures.NothingAtPosition(line, column));
        }

        var declared = new List<string>();
        foreach (var parameterGroup in function.CurriedParameterGroups)
        {
            foreach (var parameter in parameterGroup)
            {
                var name = parameter.Name?.Value;
                declared.Add(string.IsNullOrEmpty(name)
                    ? TypeText(parameter.Type)
                    : $"{name}: {TypeText(parameter.Type)}");
            }
        }

        return SignatureHelpResult.Ok(
            [
                new SignatureInfo
                {
                    Display = $"{function.DisplayName}: {TypeText(function.FullType)}",
                    Parameters = declared,
                    ActiveParameter = activeParameter,
                },
            ],
            0);
    }

    private static string TypeText(FSharpType type)
    {
        var text = type.ToString().Trim();
        return text.StartsWith("type ", StringComparison.Ordinal) ? text[5..].Trim() : text;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Expands a position to the end of the name it sits in.
    /// </summary>
    /// <param name="lineText">The line as written.</param>
    /// <param name="column">A 0-based position on that line, inside or at the end of a name.</param>
    /// <returns>The position just past the last name character.</returns>
    /// <remarks>
    /// The checker's two positional queries take <c>colAtEndOfNames</c>, not "somewhere in the name": a position in
    /// the middle of <c>total</c> resolves nothing and looks exactly like a position over whitespace. The port
    /// promises any column inside the symbol, so the adapter widens it here instead of making every caller count
    /// characters.
    /// </remarks>
    private static int NameEnd(string lineText, int column)
    {
        var index = Math.Clamp(column, 0, lineText.Length);
        while (index < lineText.Length && IsNameCharacter(lineText[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsNameCharacter(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '\'' or '.';

    private static CodeSeverity? MapSeverity(FSharpDiagnosticSeverity severity)
    {
        if (severity.IsError)
        {
            return CodeSeverity.Error;
        }

        if (severity.IsWarning)
        {
            return CodeSeverity.Warning;
        }

        return severity.IsInfo ? CodeSeverity.Info : null;
    }

    private static string? ReadLine(string fullPath, int line)
    {
        if (line < 1)
        {
            return null;
        }

        try
        {
            var index = 1;
            foreach (var text in File.ReadLines(fullPath))
            {
                if (index == line)
                {
                    return text;
                }

                index++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static async Task<T> AwaitAsync<T>(FSharpAsync<T> computation, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        var task = FSharpAsync.StartAsTask(computation, null, FSharpOption<CancellationToken>.Some(budget.Token));
        return await task.WaitAsync(budget.Token).ConfigureAwait(false);
    }

    private FSharpProjectOptions? Options(FsProject project, out CodeFailure? failure)
    {
        failure = null;

        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(project.ProjectFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stamp = DateTime.MinValue;
        }

        lock (_gate)
        {
            if (_options.TryGetValue(project.ProjectFile, out var cached) && cached.Stamp == stamp)
            {
                return cached.Options;
            }
        }

        try
        {
            var options = _checker.GetProjectOptionsFromCommandLineArgs(
                project.ProjectFile,
                [.. project.Arguments],
                null,
                null,
                null);

            lock (_gate)
            {
                _options[project.ProjectFile] = (stamp, options);
            }

            return options;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = FsFailures.CheckerFailed("The F# checker rejected the project's arguments: " + ex.Message);
            return null;
        }
    }

    private (FSharpProjectOptions? Options, string? FullPath, CodeFailure? Failure) Resolve(
        string path,
        CancellationToken cancellationToken)
    {
        if (!Supports(path))
        {
            return (null, null, FsFailures.NotFSharp(path));
        }

        if (!_workspace.TryResolveRead(path, out var fullPath, out _, out _) || !File.Exists(fullPath))
        {
            return (null, null, FsFailures.FileMissing(path));
        }

        var projectFile = _probe.Find();
        if (projectFile is null)
        {
            return (null, null, FsFailures.NoProject());
        }

        var load = _source.Load(projectFile, cancellationToken);
        if (load.Project is not { } project)
        {
            return (null, null, load.Failure ?? FsFailures.NoProject());
        }

        if (!project.Sources.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
        {
            return (null, null, FsFailures.NotAssembled(string.Create(
                CultureInfo.InvariantCulture,
                $"'{path}' is not a source of '{Path.GetFileName(project.ProjectFile)}', so the checker has nothing to say about it.")));
        }

        var options = Options(project, out var optionsFailure);
        return options is null ? (null, null, optionsFailure) : (options, fullPath, null);
    }

    private async Task<(FSharpCheckProjectResults? Results, CodeFailure? Failure)> CheckProjectAsync(
        FSharpProjectOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await AwaitAsync(_checker.ParseAndCheckProject(options, null), cancellationToken).ConfigureAwait(false);
            return (results, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, FsFailures.CheckerFailed(string.Create(
                CultureInfo.InvariantCulture,
                $"The F# checker did not finish within {Budget.TotalSeconds:0} seconds.")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, FsFailures.CheckerFailed("The F# checker failed: " + ex.Message));
        }
    }

    private async Task<(FSharpCheckFileResults? Results, CodeFailure? Failure)> CheckFileAsync(
        FSharpProjectOptions options,
        string fullPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var text = SourceText.ofString(await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false));
            var pair = await AwaitAsync(
                _checker.ParseAndCheckFileInProject(fullPath, 0, text, options, null),
                cancellationToken).ConfigureAwait(false);

            return pair.Item2 is FSharpCheckFileAnswer.Succeeded succeeded
                ? (succeeded.Item, null)
                : (null, FsFailures.CheckerFailed("The F# checker stopped before it finished this file; retry once the project builds."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, FsFailures.CheckerFailed(string.Create(
                CultureInfo.InvariantCulture,
                $"The F# checker did not finish within {Budget.TotalSeconds:0} seconds.")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, FsFailures.CheckerFailed("The F# checker failed: " + ex.Message));
        }
    }
}
