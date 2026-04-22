using System.Collections.Concurrent;

namespace AgentRp.Services;

public sealed class ActivityNotifier : IActivityNotifier
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<ActivityNotification>>> _subscriptions = [];

    public IDisposable Subscribe(string stream, Action<ActivityNotification> handler)
    {
        var handlers = _subscriptions.GetOrAdd(stream, _ => []);
        var subscriptionId = Guid.NewGuid();
        handlers[subscriptionId] = handler;
        return new Subscription(_subscriptions, stream, subscriptionId);
    }

    public void Publish(ActivityNotification notification)
    {
        if (!_subscriptions.TryGetValue(notification.Stream, out var handlers))
            return;

        foreach (var handler in handlers.Values)
            handler(notification);
    }

    private sealed class Subscription(
        ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<ActivityNotification>>> subscriptions,
        string stream,
        Guid subscriptionId) : IDisposable
    {
        public void Dispose()
        {
            if (!subscriptions.TryGetValue(stream, out var handlers))
                return;

            handlers.TryRemove(subscriptionId, out _);
        }
    }
}
