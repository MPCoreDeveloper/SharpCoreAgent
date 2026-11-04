using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpCoreAgent.Domain.Code;

namespace SharpCoreAgent.Languages.CSharp;

/// <summary>Signature help over invocations and object creation.</summary>
internal static class CsSignatureQueries
{
    public static async Task<SignatureHelpResult> GetSignatureAsync(
        CsSolutionSnapshot snapshot,
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
                return SignatureHelpResult.Failed(failure!);
            }

            if (!context.TryPosition(line, column, out var position))
            {
                return SignatureHelpResult.Failed(CsFailures.NothingAtPosition(line, column));
            }

            var token = context.Root.FindToken(position);
            var node = token.Parent?.AncestorsAndSelf().FirstOrDefault(node =>
                node is InvocationExpressionSyntax or ObjectCreationExpressionSyntax);
            var expression = node switch
            {
                InvocationExpressionSyntax invocation => invocation.Expression,
                ObjectCreationExpressionSyntax creation => creation,
                _ => null,
            };
            var arguments = node switch
            {
                InvocationExpressionSyntax invocation => invocation.ArgumentList,
                ObjectCreationExpressionSyntax creation => creation.ArgumentList,
                _ => null,
            };
            if (expression is null || arguments is null)
            {
                return SignatureHelpResult.Failed(CsFailures.NothingAtPosition(line, column));
            }

            if (!arguments.Span.Contains(position) && token.Span != arguments.CloseParenToken.Span)
            {
                return SignatureHelpResult.Failed(CsFailures.NothingAtPosition(line, column));
            }

            var info = context.Model.GetSymbolInfo(expression, cancellationToken);
            var methods = new List<IMethodSymbol>();
            if (info.Symbol is IMethodSymbol selected)
            {
                methods.Add(selected);
            }

            methods.AddRange(context.Model.GetMemberGroup(expression, cancellationToken).OfType<IMethodSymbol>()
                .Where(candidate => !methods.Contains(candidate, SymbolEqualityComparer.Default)));
            methods.AddRange(info.CandidateSymbols.OfType<IMethodSymbol>()
                .Where(candidate => !methods.Contains(candidate, SymbolEqualityComparer.Default)));
            if (methods.Count == 0)
            {
                return SignatureHelpResult.Failed(CsFailures.NothingAtPosition(line, column));
            }

            var signatures = methods.Select(method => new SignatureInfo
            {
                Display = method.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                Parameters = [.. method.Parameters.Select(Display)],
                ActiveParameter = ActiveParameter(arguments, method, position),
            }).ToArray();
            return SignatureHelpResult.Ok(signatures, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return SignatureHelpResult.Failed(CsFailures.AnalysisFailed("Roslyn could not resolve this call: " + ex.Message));
        }
    }

    private static int ActiveParameter(ArgumentListSyntax arguments, IMethodSymbol method, int position)
    {
        if (arguments.Arguments.Count == 0)
        {
            return -1;
        }

        var argument = arguments.Arguments.FirstOrDefault(argument =>
            argument.Span.Contains(position) || argument.Span.End == position);
        if (argument is null)
        {
            return position <= arguments.OpenParenToken.Span.End ? 0 : -1;
        }

        if (argument.NameColon?.Name.Identifier.ValueText is { } name)
        {
            for (var index = 0; index < method.Parameters.Length; index++)
            {
                if (method.Parameters[index].Name == name)
                {
                    return index;
                }
            }
        }

        var ordinal = arguments.Arguments.IndexOf(argument);
        return method.Parameters.Length == 0
            ? -1
            : Math.Min(ordinal, method.Parameters.Length - 1);
    }

    private static string Display(IParameterSymbol parameter)
    {
        var prefix = parameter.RefKind switch
        {
            RefKind.Ref => "ref ",
            RefKind.Out => "out ",
            RefKind.In => "in ",
            _ => parameter.IsParams ? "params " : string.Empty,
        };
        var type = parameter.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var value = parameter.HasExplicitDefaultValue && parameter.ExplicitDefaultValue is not null
            ? " = " + parameter.ExplicitDefaultValue
            : string.Empty;
        return $"{prefix}{type} {parameter.Name}{value}";
    }
}
