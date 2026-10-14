using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Exvo.CheckInService.Services;

public interface IRealtimeSubscriber : IDisposable
{
    ValueTask<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public interface IRealtimeAnalyticsNotifier
{
    void NotifyEventUpdated(int eventId);
    IRealtimeSubscriber Subscribe(int eventId);
    void Unsubscribe(int eventId, IRealtimeSubscriber subscriber);
}

public class RealtimeAnalyticsNotifier : IRealtimeAnalyticsNotifier
{
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<ChannelSubscriber, byte>> _subscribers = new();

    public void NotifyEventUpdated(int eventId)
    {
        if (_subscribers.TryGetValue(eventId, out var eventSubs))
        {
            foreach (var sub in eventSubs.Keys)
            {
                sub.Signal();
            }
        }
    }

    public IRealtimeSubscriber Subscribe(int eventId)
    {
        var eventSubs = _subscribers.GetOrAdd(eventId, _ => new ConcurrentDictionary<ChannelSubscriber, byte>());
        var sub = new ChannelSubscriber();
        eventSubs.TryAdd(sub, 0);
        return sub;
    }

    public void Unsubscribe(int eventId, IRealtimeSubscriber subscriber)
    {
        if (subscriber is ChannelSubscriber channelSub && _subscribers.TryGetValue(eventId, out var eventSubs))
        {
            eventSubs.TryRemove(channelSub, out _);
            if (eventSubs.IsEmpty)
            {
                _subscribers.TryRemove(eventId, out _);
            }
        }
    }

    private sealed class ChannelSubscriber : IRealtimeSubscriber
    {
        private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        public void Signal()
        {
            _channel.Writer.TryWrite(true);
        }

        public async ValueTask<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                return await _channel.Reader.ReadAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _channel.Writer.TryComplete();
        }
    }
}
