using System.Threading.Channels;

namespace Automation.Core.Events;

public sealed class SessionEventBus : ISessionEventBus, IDisposable
{
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private bool _disposed;

    public void Publish<TEvent>(TEvent @event)
        where TEvent : ISessionEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        Subscription[] subscribers;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            subscribers = _subscriptions.ToArray();
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.TryWrite(@event);
        }
    }

    public async IAsyncEnumerable<TEvent> Subscribe<TEvent>(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        where TEvent : ISessionEvent
    {
        var subscription = new Subscription();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscriptions.Add(subscription);
        }

        try
        {
            await foreach (var @event in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (@event is TEvent typed)
                {
                    yield return typed;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscriptions.Remove(subscription);
            }

            subscription.Complete();
        }
    }

    public void Dispose()
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Complete();
        }
    }

    private sealed class Subscription
    {
        private readonly Channel<ISessionEvent> _channel =
            Channel.CreateUnbounded<ISessionEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        public ChannelReader<ISessionEvent> Reader => _channel.Reader;

        public void TryWrite(ISessionEvent @event)
            => _channel.Writer.TryWrite(@event);

        public void Complete()
            => _channel.Writer.TryComplete();
    }
}