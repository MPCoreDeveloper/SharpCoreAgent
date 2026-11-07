using System.Threading.Channels;

namespace SharpCoreAgent.Cli;

/// <summary>
/// Fans one stream of frames out to every page that is listening.
/// </summary>
/// <remarks>
/// Each subscriber owns a bounded channel that drops the oldest frame when it is full, so a page that stops reading
/// loses events instead of stalling the turn: the loop must never wait on a browser. The channel is the one place
/// this type allocates per connection, and it is disposed when the page disconnects.
/// </remarks>
internal sealed class ServeHub
{
    /// <summary>How many frames one page may fall behind by before the oldest are dropped.</summary>
    private const int Capacity = 4096;

    private readonly object _gate = new();
    private readonly List<Subscriber> _subscribers = [];

    /// <summary>
    /// Registers a page and returns the reader its stream drains.
    /// </summary>
    /// <param name="reader">The channel to read frames from, in order.</param>
    /// <returns>A lease that unregisters the page when disposed.</returns>
    internal IDisposable Subscribe(out ChannelReader<string> reader)
    {
        var subscriber = new Subscriber();
        reader = subscriber.Channel.Reader;
        lock (_gate)
        {
            _subscribers.Add(subscriber);
        }

        return new Lease(this, subscriber);
    }

    /// <summary>
    /// Sends one frame to every page that is connected.
    /// </summary>
    /// <param name="frame">The JSON line for one event.</param>
    internal void Publish(string frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        lock (_gate)
        {
            foreach (var subscriber in _subscribers)
            {
                // TryWrite on a DropOldest channel always succeeds: the fail case is only for a completed writer.
                subscriber.Channel.Writer.TryWrite(frame);
            }
        }
    }

    private void Remove(Subscriber subscriber)
    {
        lock (_gate)
        {
            if (_subscribers.Remove(subscriber))
            {
                subscriber.Channel.Writer.TryComplete();
            }
        }
    }

    /// <summary>One page's queue of frames.</summary>
    private sealed class Subscriber
    {
        /// <summary>Gets the bounded queue; a slow reader drops its oldest frame rather than blocking the publisher.</summary>
        internal Channel<string> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<string>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    /// <summary>Removes one subscriber when the page's stream ends.</summary>
    private sealed class Lease(ServeHub hub, Subscriber subscriber) : IDisposable
    {
        private readonly ServeHub _hub = hub;
        private readonly Subscriber _subscriber = subscriber;
        private bool _disposed;

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _hub.Remove(_subscriber);
        }
    }
}
