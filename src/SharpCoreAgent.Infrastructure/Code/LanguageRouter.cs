using System.Diagnostics.CodeAnalysis;
using SharpCoreAgent.Domain.Code;

namespace SharpCoreAgent.Infrastructure.Code;

/// <summary>
/// The one code-intelligence surface the tools are handed: it asks every registered adapter which files it covers and
/// forwards the question to the first one that says yes.
/// </summary>
/// <remarks>
/// The tools never learn a language's name, so adding one is a registration rather than an edit: a new
/// <see cref="ILanguageAdapter"/> is appended here and the three tools answer for its files on the next turn. That is
/// the whole reason this type exists, and it is why the composition root registers exactly one instance under all
/// three ports instead of a per-language tool set.
/// <para>
/// The choice is made from the path alone (<see cref="ILanguageAdapter.Supports"/>), in registration order, and it is
/// ordinal: two adapters that both claim <c>.fs</c> would be a configuration mistake, and the first one winning makes
/// the mistake stable instead of random.
/// </para>
/// <para>
/// No adapter claiming a file is a <see cref="CodeFailure"/> rather than an exception, because a model that asks
/// <c>file_diagnostics</c> about a <c>.cs</c> file this build cannot check has asked a legitimate question with an
/// honest answer. The codes are <c>LANG001</c> and <c>LANG002</c> from <see cref="CodeFailures"/>.
/// </para>
/// </remarks>
public sealed class LanguageRouter : ICodeDiagnostics, ISymbolUses, ISignatureHelp
{
    private readonly ILanguageAdapter[] _adapters;

    /// <summary>
    /// Creates the router.
    /// </summary>
    /// <param name="adapters">The adapters this build has, in the order they should be offered a path.</param>
    /// <exception cref="ArgumentNullException"><paramref name="adapters"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="adapters"/> holds a null element.</exception>
    /// <remarks>
    /// An empty list is legal and means "this host has code intelligence wired but no language": every question then
    /// answers <c>LANG001</c>, which is a sentence rather than a crash.
    /// </remarks>
    public LanguageRouter(IEnumerable<ILanguageAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        _adapters = [.. adapters];

        if (Array.IndexOf(_adapters, null) >= 0)
        {
            throw new ArgumentException("A language adapter must not be null.", nameof(adapters));
        }
    }

    /// <inheritdoc />
    public async ValueTask<CodeDiagnosticsResult> GetDiagnosticsAsync(string path, CancellationToken cancellationToken)
    {
        if (!TryResolve<ICodeDiagnostics>(path, out var port, out var failure))
        {
            return CodeDiagnosticsResult.Failed(failure);
        }

        return await port.GetDiagnosticsAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<SymbolUsesResult> FindUsesAsync(
        string path,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        if (!TryResolve<ISymbolUses>(path, out var port, out var failure))
        {
            return SymbolUsesResult.Failed(failure);
        }

        return await port.FindUsesAsync(path, line, column, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<SignatureHelpResult> GetSignatureAsync(
        string path,
        int line,
        int column,
        CancellationToken cancellationToken)
    {
        if (!TryResolve<ISignatureHelp>(path, out var port, out var failure))
        {
            return SignatureHelpResult.Failed(failure);
        }

        return await port.GetSignatureAsync(path, line, column, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the adapter that owns a path and checks that it implements the port the question needs.
    /// </summary>
    /// <typeparam name="TPort">The port being asked for.</typeparam>
    /// <param name="path">The workspace-relative path.</param>
    /// <param name="port">The adapter, as the port, when it can answer.</param>
    /// <param name="failure">The reason it cannot, when it cannot.</param>
    /// <returns><see langword="true"/> when <paramref name="port"/> may be called.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty or whitespace.</exception>
    private bool TryResolve<TPort>(
        string path,
        [NotNullWhen(true)] out TPort? port,
        [NotNullWhen(false)] out CodeFailure? failure)
        where TPort : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        port = null;
        failure = null;

        foreach (var adapter in _adapters)
        {
            if (!adapter.Supports(path))
            {
                continue;
            }

            if (adapter is TPort claimed)
            {
                port = claimed;
                return true;
            }

            failure = CodeFailures.NotAnswered(path, typeof(TPort).Name);
            return false;
        }

        failure = CodeFailures.NotClaimed(path);
        return false;
    }
}
