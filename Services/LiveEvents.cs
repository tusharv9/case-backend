namespace CaseManagement.Api.Services;

using System.Collections.Concurrent;
using System.Threading.Channels;

/// <summary>One live change every open Case Management screen may want, e.g. <c>agent-status</c> with the user and their new status.</summary>
public sealed record LiveEvent(string Topic, object Data);

/// <summary>
/// Pushes live changes to every open Case Management tab (<c>GET /api/live</c>, a server-sent event stream), so a screen
/// shows them at once instead of after a refresh. In process: correct on one instance (several instances would need a shared
/// bus, as AuthService's Redis backplane does for its own hub).
/// </summary>
public interface ILiveEvents
{
    void Publish(LiveEvent liveEvent);

    /// <summary>A new listener; dispose it to stop listening.</summary>
    LiveSubscription Subscribe();
}

public sealed class LiveSubscription(ChannelReader<LiveEvent> reader, Action unsubscribe) : IDisposable
{
    public ChannelReader<LiveEvent> Reader { get; } = reader;
    public void Dispose() => unsubscribe();
}

public sealed class LiveEvents : ILiveEvents
{
    private readonly ConcurrentDictionary<Guid, Channel<LiveEvent>> _listeners = new();

    public void Publish(LiveEvent liveEvent)
    {
        foreach (var listener in _listeners.Values) listener.Writer.TryWrite(liveEvent);
    }

    public LiveSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        // A slow tab drops its oldest events rather than holding memory for ever; it re-reads the screen when it reconnects.
        var channel = Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        _listeners[id] = channel;
        return new LiveSubscription(channel.Reader, () =>
        {
            if (_listeners.TryRemove(id, out var removed)) removed.Writer.TryComplete();
        });
    }
}
