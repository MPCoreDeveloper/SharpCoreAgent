namespace SharpCoreAgent.Cli;

/// <summary>
/// Splits Ctrl+C into "stop this turn" and "leave the program".
/// </summary>
/// <remarks>
/// The first press cancels the running turn, so the partial answer and the transcript survive; with no
/// turn running — at the chat prompt, or between requests — it ends the process. One handler has to
/// know which of the two a press means, which is why both tokens live here rather than in the loop.
/// </remarks>
internal sealed class CancellationScope : IDisposable
{
    private readonly CancellationTokenSource _exit = new();
    private CancellationTokenSource? _turn;

    /// <summary>Gets the token that ends the program.</summary>
    public CancellationToken ExitToken => _exit.Token;

    /// <summary>
    /// Starts a turn.
    /// </summary>
    /// <returns>The token that cancels just this turn.</returns>
    public CancellationToken BeginTurn()
    {
        _turn?.Dispose();
        _turn = CancellationTokenSource.CreateLinkedTokenSource(_exit.Token);
        return _turn.Token;
    }

    /// <summary>Ends the running turn, if any.</summary>
    public void EndTurn()
    {
        _turn?.Dispose();
        _turn = null;
    }

    /// <summary>Handles a Ctrl+C press.</summary>
    public void HandleCancelKeyPress()
    {
        if (_turn is { IsCancellationRequested: false } turn)
        {
            turn.Cancel();
            return;
        }

        _exit.Cancel();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _turn?.Dispose();
        _exit.Dispose();
    }
}
