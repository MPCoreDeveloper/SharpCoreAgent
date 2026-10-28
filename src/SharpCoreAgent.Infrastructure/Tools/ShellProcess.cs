using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// A shell command that keeps running after the tool call that started it returned.
/// </summary>
/// <remarks>
/// A dev server or a watch-mode compiler is not a command you wait for; it is a process you start and then
/// talk to. This type owns the part that makes that safe. Output is captured into a bounded buffer the
/// caller drains, the process tree is killed when the caller stops it, when its lifetime ceiling passes and
/// when the registry is disposed, and the buffered text is the same text a blocking call would have
/// returned.
/// <para>
/// Live output is forwarded only while the call that started the process is still waiting. Once the call
/// has returned the text belongs to whoever drains it: a turn that has already ended must not keep writing
/// to a console that has moved on.
/// </para>
/// </remarks>
public sealed class ShellProcess : IDisposable
{
    /// <summary>Gets the most characters retained per process; older output is discarded, not queued.</summary>
    public const int MaxBufferedChars = 256 * 1024;

    /// <summary>Gets the marker that says earlier output was discarded by the buffer ceiling.</summary>
    public const string DroppedMarker = "… earlier output was dropped";

    private readonly Process _process;
    private readonly StringBuilder _buffer = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _completion;
    private readonly Task _pumps;
    private IToolOutputSink? _sink;
    private bool _dropped;

    internal ShellProcess(
        string id,
        string command,
        Process process,
        IToolOutputSink? sink,
        TimeSpan lifetime)
    {
        Id = id;
        Command = command;
        _process = process;
        _sink = sink;
        _lifetime = new CancellationTokenSource(lifetime);

        CloseInput(process);

        // The ceiling exists so a forgotten server cannot hold a port until the machine reboots. It is a
        // backstop, not the normal way a process ends: shell_stop and registry disposal are.
        _lifetime.Token.Register(Kill);

        var stdout = PumpAsync(process.StandardOutput);
        var stderr = PumpAsync(process.StandardError);
        _pumps = Task.WhenAll(stdout, stderr);
        _completion = WaitForExitAsync();
    }

    /// <summary>Gets the handle the model uses to talk about this process, such as <c>sh-1</c>.</summary>
    public string Id { get; }

    /// <summary>Gets the command line that was started.</summary>
    public string Command { get; }

    /// <summary>Gets a task that completes when the process has exited.</summary>
    public Task Completion => _completion;

    /// <summary>Gets the exit code once the process has exited.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>Gets a value indicating whether the process has exited.</summary>
    /// <remarks>
    /// Reading this also latches the exit code. The process can exit a moment before the completion
    /// continuation runs, so a caller that sees <see langword="true"/> here would otherwise read a null
    /// code for a process that has plainly finished.
    /// </remarks>
    public bool HasExited
    {
        get
        {
            try
            {
                if (!_process.HasExited)
                {
                    return false;
                }

                ExitCode ??= _process.ExitCode;
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // A disposed process has no exit code left to report, so it counts as finished: a caller
                // must not wait on something that can never answer again.
                return true;
            }
        }
    }

    /// <summary>
    /// Stops forwarding output to the sink, keeping it in the buffer instead.
    /// </summary>
    public void StopStreaming() => Volatile.Write(ref _sink, null);

    /// <summary>
    /// Waits for the process to exit, or for the timeout to pass.
    /// </summary>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns><see langword="true"/> when the process exited within the timeout.</returns>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);

        var delay = Task.Delay(Timeout.InfiniteTimeSpan, timer.Token);
        var winner = await Task.WhenAny(_completion, delay).ConfigureAwait(false);
        if (winner == _completion)
        {
            await _completion.ConfigureAwait(false);
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    /// <summary>
    /// Waits briefly for the output readers, then returns everything buffered since the last read.
    /// </summary>
    /// <param name="grace">How long to wait for the readers; the call stays bounded even when a grandchild holds the pipe.</param>
    /// <returns>The text, or an empty string when nothing new was printed.</returns>
    public async Task<string> DrainAsync(TimeSpan grace)
    {
        // The delay is cancellable, so the timer is released the moment the readers finish instead of
        // outliving the call by the whole grace period.
        using var timer = new CancellationTokenSource(grace);
        await Task.WhenAny(_pumps, Task.Delay(Timeout.InfiniteTimeSpan, timer.Token)).ConfigureAwait(false);
        return Drain();
    }

    /// <summary>
    /// Returns everything buffered since the last read and empties the buffer.
    /// </summary>
    /// <returns>The text, or an empty string when nothing new was printed.</returns>
    public string Drain()
    {
        lock (_gate)
        {
            var text = _buffer.ToString();
            _buffer.Clear();

            if (_dropped)
            {
                text = string.Concat(DroppedMarker, "\n", text);
                _dropped = false;
            }

            return text;
        }
    }

    /// <summary>
    /// Kills the process tree.
    /// </summary>
    /// <returns><see langword="true"/> when a running process was killed.</returns>
    public bool TryStop()
    {
        if (HasExited)
        {
            return false;
        }

        Kill();
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        StopStreaming();
        TryStop();
        _lifetime.Dispose();
        _process.Dispose();
    }

    /// <inheritdoc />
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Id}: {Command}");

    /// <summary>
    /// Hands a new process end-of-input on its standard input.
    /// </summary>
    /// <param name="process">The process that was just started.</param>
    /// <remarks>
    /// The stream is redirected when it is created and closed here, which is what makes it end-of-input
    /// rather than an empty pipe a reader would wait on forever. A command typed by a model is not a
    /// conversation partner: it must never be able to read the line the developer is about to type at the
    /// CLI's own prompt, and one that asks a question should fail fast instead of holding a turn open.
    /// </remarks>
    private static void CloseInput(Process process)
    {
        if (!process.StartInfo.RedirectStandardInput)
        {
            return;
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            // The command exited before the pipe was opened. There is nobody left to ask either way.
        }
    }

    private async Task PumpAsync(StreamReader reader)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            Append(line);

            if (Volatile.Read(ref _sink) is { } sink)
            {
                try
                {
                    await sink.WriteAsync(string.Concat(line, "\n"), CancellationToken.None).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // A renderer that fails must not turn into a command that fails.
                catch (Exception)
#pragma warning restore CA1031
                {
                    // The text is still in the buffer, so the model loses nothing but the live view.
                }
            }
        }
    }

    private void Append(string line)
    {
        lock (_gate)
        {
            if (_buffer.Length > MaxBufferedChars)
            {
                // Discard rather than grow: a process that prints faster than the agent reads must cost a
                // bounded amount of memory, and the marker tells the reader why the log has a hole.
                _buffer.Clear();
                _dropped = true;
            }

            _buffer.Append(line).Append('\n');
        }
    }

    private async Task WaitForExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            ExitCode = _process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Killed before it could report; the caller sees that through shell_output and nothing breaks.
        }
    }

    private void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or AggregateException or Win32Exception or NotSupportedException or ObjectDisposedException)
        {
            // Already gone, or a child is unreachable. Either way there is nothing left to stop.
        }
    }



}
