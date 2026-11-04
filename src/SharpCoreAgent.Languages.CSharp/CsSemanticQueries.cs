using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using SharpCoreAgent.Domain.Code;
using AgentWorkspace = SharpCoreAgent.Domain.Workspace;

namespace SharpCoreAgent.Languages.CSharp;

/// <summary>Compiler-bound uses over one loaded document.</summary>
internal static class CsSemanticQueries
{
    public static async Task<SymbolUsesResult> FindUsesAsync(
        CsSolutionSnapshot snapshot,
        AgentWorkspace workspace,
        string fullPath,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        try
        {
            var (context, failure) = await CsDocumentContext.LoadAsync(snapshot, fullPath, cancellationToken).ConfigureAwait(false);
            if (context is null)
            {
                return SymbolUsesResult.Failed(failure!);
            }

            var (symbol, symbolFailure) = ResolveSymbol(context, line, column);
            if (symbol is null)
            {
                return SymbolUsesResult.Failed(symbolFailure!);
            }

            var subject = Normalize(symbol);
            var definitions = new HashSet<string>(StringComparer.Ordinal);
            var uses = new Dictionary<string, SymbolUse>(StringComparer.Ordinal);
            foreach (var location in subject.Locations.Where(location => location.IsInSource))
            {
                Add(location, true);
            }

            var references = await SymbolFinder.FindReferencesAsync(subject, snapshot.Solution, cancellationToken)
                .ConfigureAwait(false);
            foreach (var referenced in references)
            {
                foreach (var location in referenced.Definition.Locations.Where(location => location.IsInSource))
                {
                    Add(location, true);
                }

                foreach (var location in referenced.Locations
                    .Where(location => !location.IsImplicit)
                    .Select(location => location.Location))
                {
                    Add(location, definitions.Contains(Key(location)));
                }
            }

            var ordered = uses.Values
                .OrderBy(use => use.Path, StringComparer.Ordinal)
                .ThenBy(use => use.Line)
                .ThenBy(use => use.Column)
                .ToArray();
            return SymbolUsesResult.Ok(ordered);

            void Add(Location location, bool isDefinition)
            {
                if (location.SourceTree?.FilePath is not { } path || !workspace.IsInside(path))
                {
                    return;
                }

                var position = location.GetLineSpan().StartLinePosition;
                var symbolUse = new SymbolUse
                {
                    Path = workspace.ToRelative(path).Replace('\\', '/'),
                    Line = position.Line + 1,
                    Column = position.Character,
                    IsDefinition = isDefinition,
                    Display = Display(subject),
                };
                var key = $"{symbolUse.Path}|{symbolUse.Line}|{symbolUse.Column}";
                if (uses.TryGetValue(key, out var existing))
                {
                    uses[key] = existing with { IsDefinition = existing.IsDefinition || isDefinition };
                }
                else
                {
                    uses[key] = symbolUse;
                    if (isDefinition)
                    {
                        definitions.Add(Key(location));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SymbolUsesResult.Failed(CsFailures.AnalysisFailed("Roslyn could not resolve this symbol: " + ex.Message));
        }
    }

    private static (ISymbol? Symbol, CodeFailure? Failure) ResolveSymbol(CsDocumentContext context, int line, int column)
    {
        if (!context.TryPosition(line, column, out var position))
        {
            return (null, CsFailures.NothingAtPosition(line, column));
        }

        var token = context.Root.FindToken(position);
        if (!token.Span.Contains(position) && token.Span.End != position)
        {
            return (null, CsFailures.NothingAtPosition(line, column));
        }

        for (var node = token.Parent; node is not null; node = node.Parent)
        {
            var declared = context.Model.GetDeclaredSymbol(node);
            if (declared is not null)
            {
                return (declared, null);
            }

            var info = context.Model.GetSymbolInfo(node);
            if (info.Symbol is not null)
            {
                return (info.Symbol, null);
            }

            var candidates = info.CandidateSymbols.Where(symbol => symbol is not null).ToArray();
            if (candidates.Length == 1)
            {
                return (candidates[0], null);
            }

            if (node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or MemberAccessExpressionSyntax)
            {
                break;
            }
        }

        return (null, CsFailures.NothingAtPosition(line, column));
    }

    private static ISymbol Normalize(ISymbol symbol)
    {
        var normalized = symbol is IAliasSymbol alias ? alias.Target : symbol;
        return (normalized is IMethodSymbol method ? method.ReducedFrom ?? method : normalized).OriginalDefinition;
    }

    private static string Display(ISymbol symbol) =>
        symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor
            ? constructor.ContainingType.Name
            : symbol.Name;

    private static string Key(Location location)
    {
        var path = location.SourceTree?.FilePath ?? string.Empty;
        var position = location.GetLineSpan().StartLinePosition;
        return $"{path}|{position.Line}|{position.Character}";
    }
}
