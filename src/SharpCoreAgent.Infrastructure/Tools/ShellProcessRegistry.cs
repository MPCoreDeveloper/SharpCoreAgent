using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using SharpCoreAgent.Domain.Tools;

namespace SharpCoreAgent.Infrastructure.Tools;

/// <summary>
/// The commands this process started and has not cleaned up yet.
/// </summary>
/// <remarks>
/// Without a registry a background command is a leak: <c>cmd.exe</c> is not killed when its parent exits, so
/// a dev server started by one turn would still hold its port the next morning. The registry gives every
/// process a short handle the model can name in a later call, keeps at most
/// <see cref="MaxTracked"/> finished ones for their exit codes, and kills everything it still owns when the
/// container is disposed or the host process exits.
/// <para>
/// The registry is a container singleton because the processes outlive the tool call that started them. It
/// is not static, because a test that starts a process must be able to end it without ending the test run.
/// </para>
/// </remarks>
public sealed class ShellProcessRegistry : IDisposable
{
    /// <summary>Gets how long a background process may run before the registry kills it.</summary>
    /// <remarks>
    /// One hour: long enough for a test suite or a dev server to be useful, short enough that a forgotten
    /// process does not survive to the next working day. <c>shell_stop</c> is the normal way to end one.
    /// </remarks>
    public static TimeSpan MaxBackgroundLifetime { get; } = TimeSpan.FromHours(1);

    /// <summary>Gets the most processes the registry remembers, including exited ones.</summary>
    public const int MaxTracked = 32;

    private readonly Dictionary<string, ShellProcess> _processes = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly object _gate = new();
    private int _next;
    private bool _disposed;

    /// <summary>
    /// Creates a registry that also cleans up when the host process exits.
    /// </summary>
    public ShellProcessRegistry() => AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

    /// <summary>Gets the handles of the processes that are still running.</summary>
    public IReadOnlyList<string> Running
    {
        get
        {
            lock (_gate)
            {
                return [.. _processes.Values.Where(process => !process.HasExited).Select(process => process.Id)];
            }
        }
    }

    /// <summary>
    /// Starts a process and takes ownership of it.
    /// </summary>
    /// <param name="command">The command line, for display.</param>
    /// <param name="startInfo">How to start it; the caller has already removed credentials from the environment.</param>
    /// <param name="sink">Where to forward output while the starting call is still waiting.</param>
    /// <param name="lifetime">The ceiling after which the process is killed.</param>
    /// <returns>The tracked process.</returns>
    /// <exception cref="InvalidOperationException">The process could not be started, or the registry is disposed.</exception>
    /// <exception cref="Win32Exception">The shell executable does not exist.</exception>
    public ShellProcess Start(string command, ProcessStartInfo startInfo, IToolOutputSink? sink, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(startInfo);

        string id;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _next++;
            id = string.Create(CultureInfo.InvariantCulture, $"sh-{_next}");
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture, $"could not start '{startInfo.FileName}'"));
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw;
        }

        var tracked = new ShellProcess(id, command, process, sink, lifetime);
        lock (_gate)
        {
            _processes[id] = tracked;
            _order.Add(id);
            PruneLocked();
        }

        return tracked;
    }

    /// <summary>
    /// Finds a tracked process by handle, including one that has already exited.
    /// </summary>
    /// <param name="id">The handle, such as <c>sh-1</c>.</param>
    /// <param name="process">The process, when the handle is known.</param>
    /// <returns><see langword="true"/> when the handle names a process this registry started.</returns>
    public bool TryGet(string id, out ShellProcess process)
    {
        lock (_gate)
        {
            return _processes.TryGetValue(id, out process!);
        }
    }

    /// <summary>
    /// Kills a tracked process tree.
    /// </summary>
    /// <param name="id">The handle.</param>
    /// <returns><see langword="true"/> when the handle was known; the process may already have exited.</returns>
    public bool Stop(string id)
    {
        if (!TryGet(id, out var process))
        {
            return false;
        }

        process.TryStop();
        return true;
    }
    /// <inheritdoc />
    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

        ShellProcess[] processes;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            processes = [.. _processes.Values];
            _processes.Clear();
            _order.Clear();
        }

        foreach (var process in processes)
        {
            process.Dispose();
        }
    }

    private void PruneLocked()
    {
        while (_order.Count > MaxTracked)
        {
            var index = _order.FindIndex(id => _processes[id].HasExited);
            if (index < 0)
            {
                // Every tracked process is still running: the ceiling keeps memory bounded, so the oldest
                // entry stays until one of them exits rather than a live process losing its handle.
                return;
            }

            var id = _order[index];
            _order.RemoveAt(index);
            if (_processes.Remove(id, out var finished))
            {
                finished.Dispose();
            }
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        // Handlers run while the runtime is shutting down, so this does the one thing that matters: it
        // takes the children down with the parent instead of leaving them holding ports and file locks.
        ShellProcess[] processes;
        lock (_gate)
        {
            processes = [.. _processes.Values];
        }

        foreach (var process in processes)
        {
            process.TryStop();
        }
    }
}

