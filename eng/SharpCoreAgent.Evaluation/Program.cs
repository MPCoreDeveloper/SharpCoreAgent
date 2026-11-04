namespace SharpCoreAgent.Evaluation;

/// <summary>Owns Ctrl+C without introducing a global Program type into hosts that reference the harness.</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        void Cancel(object? sender, ConsoleCancelEventArgs press)
        {
            press.Cancel = true;
            cancellation.Cancel();
        }

        Console.CancelKeyPress += Cancel;
        try
        {
            return await EvaluationCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= Cancel;
        }
    }
}
